using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Hosting;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Extensions.Logging;
using System;
using Microsoft.ApplicationInsights.Extensibility;

namespace OneBoxDeployment.Api
{
    /// <summary>
    /// The ASP.NET Core program startup class.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// The ASP.NET Core entry point.
        /// </summary>
        /// <param name="args">The command line arguments used to run this application.</param>
        public static void Main(string[] args)
        {
            BuildWebHost(args).Run();
        }


        /// <summary>
        /// The ASP.NET Core conventional build method.
        /// </summary>
        /// <param name="args">The command line arguments used to run this application.</param>
        /// <returns>The command line arguments used to run this application.</returns>
        public static IHost BuildWebHost(string[] args) =>
            InternalBuildWebHost(args, new Dictionary<string, string>(), Enumerable.Empty<ILoggerProvider>());


        /// <summary>
        /// The ASP.NET Core conventional build method.
        /// </summary>
        /// <param name="args">The command line arguments used to run this application.</param>
        /// <param name="settingsOverrides">A collection of well-known key-value configuration values for the application.
        /// These settings are applied after appsettings.{env.EnvironmentName}.json, so they override them.</param>
        /// <param name="loggerProviders">Extra providers to be added to the system.</param>
        /// <returns>The command line arguments used to run this application.</returns>
        public static IHost InternalBuildWebHost(string[] args, Dictionary<string, string> settingsOverrides, IEnumerable<ILoggerProvider> loggerProviders)
        {
            //The initial build needs to be done like this to include command line arguments.
            //See bug https://github.com/aspnet/KestrelHttpServer/issues/639.
            var commandLineConfig = new ConfigurationBuilder()
                .AddCommandLine(args).Build();

            //TODO: https://github.com/aspnet/Extensions/blob/master/src/Hosting/Hosting/src/Host.cs.
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((builderContext, config) =>
                {
                    var env = builderContext.HostingEnvironment;
                    config
                        .AddJsonFile("appsettings.api.json", optional: false, reloadOnChange: true)
                        .AddJsonFile($"appsettings.api.{env.EnvironmentName}.json", optional: false, reloadOnChange: true);

                    if(env.IsDevelopment())
                    {
                        var appAssembly = Assembly.Load(new AssemblyName(env.ApplicationName));
                        if(appAssembly != null)
                        {
                            config.AddUserSecrets(appAssembly, optional: true);
                        }
                    }

                    config.AddEnvironmentVariables();

                    if(args != null)
                    {
                        config.AddCommandLine(args);
                    }

                    config.AddInMemoryCollection(settingsOverrides);
                })
                .ConfigureLogging((hostingContext, loggingBuilder) =>
                {
                    var section = hostingContext.Configuration.GetSection("Serilog");
                    var loggerConfiguration = new LoggerConfiguration().ReadFrom.Configuration(section);
                    var logger = loggerConfiguration.CreateLogger();
                    loggingBuilder.AddSerilog(logger, dispose: true);

                    foreach(var loggerProvider in loggerProviders)
                    {
                        loggingBuilder.AddProvider(loggerProvider);
                    }
                })
               .ConfigureServices((hostContext, serviceCollection) =>
               {
                   /*if(loggerProviders?.Any() == false)
                   {
                       //This is the same as .AddSerilog, see at
                       //https://github.com/serilog/serilog-aspnetcore/blob/81af9a71e162ea817a1c03abc38703c3cfe4ac15/src/Serilog.AspNetCore/SerilogWebHostBuilderExtensions.cs.
                       //The reason is that this way this can be added or left out when
                       //testing where non-Serilog sink is used.
                       serviceCollection.AddSingleton<ILoggerFactory>(services =>
                       {
                           var factory = new SerilogLoggerFactory(Log.Logger, true);
                           foreach(var provider in services.GetServices<ILoggerProvider>())
                           {
                               factory.AddProvider(provider);
                           }

                           return factory;
                       });
                   }*/

                   //serviceCollection.AddSingleton<Microsoft.Extensions.Logging.ILogger>(svc => svc.GetRequiredService<ILogger<Startup>>());
                   //serviceCollection.AddSingleton<Microsoft.Extensions.Logging.ILogger>(provider =>
                   //    provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Startup>>());
                   //serviceCollection.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<Startup>), typeof(Logger<Startup>));
                   //var diagnosticContext = new DiagnosticContext(Log.Logger);
                   //serviceCollection.AddSingleton(diagnosticContext);
                   //serviceCollection.AddSingleton<IDiagnosticContext>(diagnosticContext);
               })
               .UseSerilog((HostBuilderContext context, IServiceProvider services, LoggerConfiguration loggerConfiguration) =>
                    loggerConfiguration
                    .ReadFrom
                    .Configuration(context.Configuration)
                    .WriteTo
                    .ApplicationInsights(services.GetRequiredService<TelemetryConfiguration>(), TelemetryConverter.Traces), preserveStaticLogger: false, writeToProviders: true)
               .ConfigureWebHostDefaults(webBuilder =>

                webBuilder.UseConfiguration(commandLineConfig)
                .UseKestrel(options => options.AddServerHeader = false)
                .UseContentRoot(Directory.GetCurrentDirectory())
                .UseStartup<Startup>()


                .UseUrls(args[1])
                .UseIISIntegration()
                .CaptureStartupErrors(true)
                .UseSetting(WebHostDefaults.ApplicationKey, typeof(Startup).GetTypeInfo().Assembly.FullName)
                .UseDefaultServiceProvider((context, options) => options.ValidateScopes = !context.HostingEnvironment.IsProduction())
                ).Build();
        }
    }
}
