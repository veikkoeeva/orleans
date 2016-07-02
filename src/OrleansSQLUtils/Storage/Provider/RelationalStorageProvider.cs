using Newtonsoft.Json;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.SqlUtils;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;


namespace Samples.StorageProviders
{
    /// <summary>
    /// The queries currently used by the <see cref="RelationalStorageProvider"/>.
    /// </summary>
    public class RelationalStorageProviderQueries
    {
        /// <summary>
        /// The clause to write to the storage.
        /// </summary>
        public string WriteToStorage { get; }

        /// <summary>
        /// The clause to read from the storage.
        /// </summary>
        public string ReadFromStorage { get; set; }

        /// <summary>
        /// The clause to clear the storage.
        /// </summary>
        public string ClearState { get; set; }


        public RelationalStorageProviderQueries(string writeToStorage, string readFromStorage, string clearState)
        {
            if(writeToStorage == null)
            {
                throw new ArgumentNullException(nameof(writeToStorage));
            }

            if(readFromStorage == null)
            {
                throw new ArgumentNullException(nameof(readFromStorage));
            }

            if(clearState == null)
            {
                throw new ArgumentNullException(nameof(clearState));
            }

            WriteToStorage = writeToStorage;
            ReadFromStorage = readFromStorage;
            ClearState = clearState;
        }
    }

    /// <summary>
    /// Logging codes used by <see cref="RelationalStorageProvider"/>.
    /// </summary>
    /// <remarks> These are taken from <em>Orleans.Providers.ProviderErrorCode</em> and <em>Orleans.Providers.AzureProviderErrorCode</em>.</remarks>
    internal enum RelationalStorageProviderCodes
    {
        //These is from Orleans.Providers.ProviderErrorCode and Orleans.Providers.AzureProviderErrorCode.
        ProvidersBase = 200000,

        RelationalProviderBase = ProvidersBase + 400,
        RelationalProviderDeleteError = RelationalProviderBase + 8,
        RelationalProviderInitProvider = RelationalProviderBase + 9,
        RelationalProviderNoDeserializer = RelationalProviderBase + 10,
        RelationalProviderNoStateFound = RelationalProviderBase + 11,
        RelationalProviderClearing = RelationalProviderBase + 12,
        RelationalProviderCleared = RelationalProviderBase + 13,
        RelationalProviderReading = RelationalProviderBase + 14,
        RelationalProviderRead = RelationalProviderBase + 15,
        RelationalProviderReadError = RelationalProviderBase + 16,
        RelationalProviderWriting = RelationalProviderBase + 17,
        RelationalProviderWrote = RelationalProviderBase + 18,
        RelationalProviderWriteError = RelationalProviderBase + 19
    }


    /// <summary>
    /// A strategy to pick a serializer or a deserializer for storage operations. As for an example, this can be used to:
    /// 1) Add a custom serializer or deserializer for use in storage provider operations (e.g. ProtoBuf or something else).
    /// 2) In combination with serializer or deserializer to update stored object version.
    /// 3) Per-grain storage format selection
    /// 4) Switch storage format first by reading using the save format and then writing in the new format.
    /// </summary>
    public interface IStorageSerializationPicker
    {
        /// <summary>
        /// The configured deserializers.
        /// </summary>
        ICollection<IStorageDeserializer> Deserializers { get; }

        /// <summary>
        /// The configured serializers.
        /// </summary>
        ICollection<IStorageSerializer> Serializers { get; }

        /// <summary>Picks a deserializer using the given parameters.</summary>
        /// <param name="serviceId">The ID of the current service.</param>
        /// <param name="storageProviderInstanceName">The requesting storage provider.</param>
        /// <param name="grainType">The type of grain.</param>
        /// <param name="grainReference">The grain reference.</param>
        /// <param name="grainState">The grain state.</param>
        /// <param name="tag">An optional tag parameter that might be used by the storage parameter for "out-of-band" contracts.</param>
        /// <returns>A serializer or <em>null</em> if not match was found.</returns>
        IStorageDeserializer PickDeserializer(string serviceId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState grainState, string tag = null);

        /// <summary>Picks a serializer using the given parameters.</summary>
        /// <param name="serviceId">The ID of the current service.</param>
        /// <param name="storageProviderInstanceName">The requesting storage provider.</param>
        /// <param name="grainType">The type of grain.</param>
        /// <param name="grainReference">The grain reference.</param>
        /// <param name="grainState">The grain state.</param>
        /// <param name="tag">An optional tag parameter that might be used by the storage parameter for "out-of-band" contracts.</param>
        /// <returns>A deserializer or <em>null</em> if not match was found.</returns>
        IStorageSerializer PickSerializer(string serviceId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState grainState, string tag = null);
    }


    /// <summary>
    /// A strategy to pick a serializer or a deserializer for storage operations. This can be used to:
    /// 1) Add a custom serializer or deserializer for use in storage provider operations.
    /// 2) In combination with serializer or deserializer to update stored object version.
    /// 3) Per-grain storage format selection
    /// 4) Switch storage format first by reading using the save format and then writing in the new format.
    /// </summary>
    public class DefaultRelationalStoragePicker: IStorageSerializationPicker
    {
        /// <summary>
        /// The configured deserializers.
        /// </summary>
        public ICollection<IStorageDeserializer> Deserializers { get; }

        /// <summary>
        /// The configured serializers.
        /// </summary>
        public ICollection<IStorageSerializer> Serializers { get; }


        /// <summary>
        /// Constructs the serializers from the given configuration properties.
        /// </summary>
        /// <param name="deserializers">The deserializers to be used.</param>
        /// <param name="serializers">The serializers to be used.</param>
        public DefaultRelationalStoragePicker(IEnumerable<IStorageDeserializer> deserializers, IEnumerable<IStorageSerializer> serializers)
        {
            if(deserializers == null)
            {
                throw new ArgumentNullException(nameof(deserializers));
            }

            if(serializers == null)
            {
                throw new ArgumentNullException(nameof(serializers));
            }

            Deserializers = new Collection<IStorageDeserializer>(deserializers.ToList());
            Serializers = new Collection<IStorageSerializer>(serializers.ToList());
        }


        /// <summary>
        /// Picks a deserializer using the given parameters.
        /// <see cref="IStorageSerializationPicker.PickDeserializer"/>
        /// </summary>
        public IStorageDeserializer PickDeserializer(string serviceId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState grainState, string tag = null)
        {
            //If the tag has been given, try to pick that one and if not found, take the first on the list. This arrangement allows one to switch storage format more easily.
            var deserializer = Deserializers.FirstOrDefault(i => i.Tag == tag);
            return deserializer ?? Deserializers.FirstOrDefault();
        }


        /// <summary>
        /// Picks a serializer using the given parameters.
        /// <see cref="IStorageSerializationPicker.PickSerializer"/>
        /// </summary>
        public IStorageSerializer PickSerializer(string servideId, string storageProviderInstanceName, string grainType, GrainReference grainReference, IGrainState grainState, string tag = null)
        {
            var serializer = Serializers.FirstOrDefault(i => i.Tag == tag);
            return serializer ?? Serializers.FirstOrDefault();
        }
    }


    /// <summary>
    /// A canonical interface for a storage provider serializer.
    /// </summary>
    public interface IStorageSerializer
    {
        /// <summary>
        /// Can this provider stream data.
        /// </summary>
        bool CanStream { get; }

        /// <summary>
        /// An optional tag that a <see cref="IStorageSerializationPicker"/> or <see cref="IStorageProvider"/> provider can use to pick this serializer.
        /// </summary>
        string Tag { get; }

        /// <summary>
        /// Serializes the given data.
        /// </summary>
        /// <param name="data">The data to be serialized.</param>
        /// <returns>The serialized data.</returns>
        object Serialize(object data);

        /// <summary>
        /// Serializes the given data to a stream.
        /// </summary>
        /// <param name="dataStream">The stream to serialize to.</param>
        /// <param name="data">The data to serialize.</param>
        /// <returns>The stream to which data was serialized. The same as <paramref name="dataStream"/>.</returns>
        object Serialize(Stream dataStream, object data);
    }


    /// <summary>
    /// A canonical interface for a storage provider deserializer.
    /// </summary>
    public interface IStorageDeserializer
    {
        /// <summary>
        /// Can this provider stream data.
        /// </summary>
        bool CanStream { get; }

        /// <summary>
        /// An optional tag that a <see cref="IStorageSerializationPicker"/> or <see cref="IStorageProvider"/> provider can use to pick a deserializer.
        /// </summary>
        string Tag { get; }

        /// <summary>
        /// Deserializes the given data.
        /// </summary>
        /// <param name="data">The data to be serialized.</param>
        /// <param name="grainStateType">The type of the grain state.</param>
        /// <returns>The deserialized object.</returns>
        object Deserialize(object data, Type grainStateType);

        /// <summary>
        /// Deserializes the given data from a stream.
        /// </summary>
        /// <param name="dataStream">The stream from which to serialize.</param>
        /// <param name="grainStateType">The type of the grain state.</param>
        /// <returns>The deserialized object.</returns>
        object Deserialize(Stream dataStream, Type grainStateType);
    }


    /// <summary>
    /// A default JSON serializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultJsonSerializer: IStorageSerializer
    {
        /// <summary>
        /// The serializer this uses for reference purposes.
        /// </summary>
        public JsonSerializer Serializer { get; }

        /// <summary>
        /// The settings this serializer uses for reference purposes.
        /// </summary>
        public JsonSerializerSettings Settings { get; }

        /// <summary>
        /// <see cref="IStorageSerializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageSerializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="settings">The settings which was used to construct a JSON serializer.</param>
        /// <param name="tag"><see cref="IStorageSerializer.Tag"/>.</param>
        public OrleansStorageDefaultJsonSerializer(JsonSerializerSettings settings, string tag)
        {
            if(settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Serializer = JsonSerializer.Create(settings);
            Settings = settings;
            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(Stream, object)"/>
        /// </summary>
        public object Serialize(Stream dataStream, object data)
        {
            using(var streamWriter = new StreamWriter(dataStream))
            {
                using(JsonTextWriter writer = new JsonTextWriter(streamWriter))
                {
                    Serializer.Serialize(writer, data);
                    writer.Flush();
                }
            }

            return dataStream;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(object)"/>
        /// </summary>
        public object Serialize(object data)
        {
            return JsonConvert.SerializeObject(data, Settings);
        }
    }


    /// <summary>
    /// A default JSON deserializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultJsonDeserializer: IStorageDeserializer
    {
        /// <summary>
        /// The deserializer this uses for reference purposes.
        /// </summary>
        public JsonSerializer Deserializer { get; }

        /// <summary>
        /// The settings this deserializer uses for reference purposes.
        /// </summary>
        public JsonSerializerSettings Settings { get; }

        /// <summary>
        /// <see cref="IStorageDeserializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageDeserializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="settings">The settings which was used to construct a JSON deserializer.</param>
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultJsonDeserializer(JsonSerializerSettings settings, string tag)
        {
            if(settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Deserializer = JsonSerializer.Create(settings);
            Settings = settings;
            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(Stream, Type)"/>.
        /// </summary>
        public object Deserialize(Stream dataStream, Type grainStateType)
        {
            using(var streamReader = new StreamReader(dataStream))
            {
                using(var reader = new JsonTextReader(streamReader))
                {
                    return Deserializer.Deserialize(reader, grainStateType);
                }
            }
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            return JsonConvert.DeserializeObject((string)data, grainStateType, Settings);
        }
    }


    /// <summary>
    /// A default XML serializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultXmlSerializer: IStorageSerializer
    {
        /// <summary>
        /// <see cref="IStorageSerializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageSerializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageSerializer.Tag"/>.</param>
        public OrleansStorageDefaultXmlSerializer(string tag)
        {
            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(Stream, object)"/>.
        /// </summary>
        public object Serialize(Stream dataStream, object data)
        {
            using(StreamWriter writer = new StreamWriter(dataStream))
            {
                var serializer = new XmlSerializer(data.GetType());
                serializer.Serialize(writer, data);
            }

            return dataStream;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(object)"/>.
        /// </summary>
        public object Serialize(object data)
        {
            using(StringWriter writer = new StringWriter())
            {
                var serializer = new XmlSerializer(data.GetType());
                serializer.Serialize(writer, data);

                return writer.ToString();
            }
        }
    }


    /// <summary>
    /// A default XML deserializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultXmlDeserializer: IStorageDeserializer
    {
        /// <summary>
        /// <see cref="IStorageDeserializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = true;

        /// <summary>
        /// <see cref="IStorageDeserializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this deserializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultXmlDeserializer(string tag)
        {
            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(Stream, Type)"/>.
        /// </summary>
        public object Deserialize(Stream dataStream, Type grainStateType)
        {
            var serializer = new XmlSerializer(grainStateType);
            return serializer.Deserialize(dataStream);
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            var deserializer = new XmlSerializer(grainStateType);
            using(var reader = new StringReader((string)data))
            {
                return deserializer.Deserialize(reader);
            }
        }
    }


    /// <summary>
    /// A default binary serializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultBinarySerializer: IStorageSerializer
    {
        /// <summary>
        /// <see cref="IStorageSerializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = false;

        /// <summary>
        /// <see cref="IStorageSerializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this serializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageSerializer.Tag"/>.</param>
        public OrleansStorageDefaultBinarySerializer(string tag)
        {
            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }

            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(Stream, object)"/>.
        /// </summary>
        /// <exception cref="NotSupportedException"/>
        public object Serialize(Stream stream, object data)
        {
            throw new NotSupportedException($"{nameof(OrleansStorageDefaultBinarySerializer)} does not support stream deserialization.");
        }


        /// <summary>
        /// <see cref="IStorageSerializer.Serialize(object)"/>.
        /// </summary>
        public object Serialize(object data)
        {
            return SerializationManager.SerializeToByteArray(data);
        }
    }


    /// <summary>
    /// A default binary deserializer for storage providers.
    /// </summary>
    [DebuggerDisplay("CanStream = {CanStream}, Tag = {Tag}")]
    public class OrleansStorageDefaultBinaryDeserializer: IStorageDeserializer
    {
        /// <summary>
        /// <see cref="IStorageDeserializer.CanStream"/>
        /// </summary>
        public bool CanStream { get; } = false;

        /// <summary>
        /// <see cref="IStorageDeserializer.Tag"/>
        /// </summary>
        public string Tag { get; }


        /// <summary>
        /// Constructs this deserializer from the given parameters.
        /// </summary>
        /// <param name="tag"><see cref="IStorageDeserializer.Tag"/>.</param>
        public OrleansStorageDefaultBinaryDeserializer(string tag)
        {
            if(string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("The parameter should contain characters.", nameof(tag));
            }
            Tag = tag;
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(Stream, Type)"/>.
        /// </summary>
        /// <exception cref="NotSupportedException"/>
        public object Deserialize(Stream dataStream, Type grainStateType)
        {
            throw new NotSupportedException($"{nameof(OrleansStorageDefaultBinaryDeserializer)} does not support stream deserialization.");
        }


        /// <summary>
        /// <see cref="IStorageDeserializer.Deserialize(object, Type)"/>.
        /// </summary>
        public object Deserialize(object data, Type grainStateType)
        {
            return SerializationManager.DeserializeFromByteArray<object>((byte[])data);
        }
    }


    [DebuggerDisplay("Name = {Name}, ConnectionString = {Storage.ConnectionString}")]
    public class RelationalStorageProvider: IStorageProvider
    {
        /// <summary>
        /// The default query to initialize this structure from the Orleans database.
        /// </summary>
        public const string DefaultInitializationQuery = "SELECT QueryKey, QueryText FROM OrleansQuery WHERE QueryKey = 'WriteToStorageKey' OR QueryKey = 'ReadFromStorageKey' OR QueryKey = 'ClearStorageKey';";

        /// <summary>
        /// The default ADO.NET invariant used for storage if none is given. This corresponds to Orleans.Runtime.Constants.INVARIANT_NAME_SQL_SERVER.
        /// </summary>
        private const string DefaultAdoInvariantInvariantPropertyName = "System.Data.SqlClient";

        /// <summary>
        /// The canonical constant for storage provider connection string. Corresponds to Orleans.Runtime.Constants.DATA_CONNECTION_STRING_NAME.
        /// </summary>
        private const string DataConnectionStringPropertyName = "DataConnectionString";

        /// <summary>
        /// The canonical constant for relational storage provider connection string. Corresponds to Orleans.Runtime.Constants.ADO_INVARIANT_NAME.
        /// </summary>
        private const string DataConnectionInvariantPropertyName = "AdoInvariant";

        /// <summary>
        /// A strategy to pick a serializer or a deserializer for storage operations. This can be used to:
        /// 1) Add a custom serializer or deserializer for use in storage provider operations.
        /// 2) In combination with serializer or deserializer to update stored object version.
        /// 3) Per-grain storage format selection
        /// 4) Switch storage format first by reading using the save format and then writing in the new format.
        /// </summary>
        private IStorageSerializationPicker StorageSerializationPicker { get; set; }

        /// <summary>
        /// The storage used for back-end operations.
        /// </summary>
        private IRelationalStorage Storage { get; set; }

        /// <summary>
        /// The Service ID for which this relational provider is used.
        /// </summary>
        private string ServiceId { get; set; }

        /// <summary>
        /// A slot key for storage string payload formatted in JSON.
        /// </summary>
        public const string UseJsonFormatPropertyName = "UseJsonFormat";

        /// <summary>
        /// A slot key for storage string payload formatted in XML.
        /// </summary>
        public const string UseXmlFormatPropertyName = "UseXmlFormat";

        /// <summary>
        /// A slot key for storage binary payload. The format can be native Orleans binary format, Bond or something user configured.
        /// </summary>
        public const string UseBinaryFormatPropertyName = "UseBinaryFormat";

        /// <summary>
        /// The queries currently used. When this is updated, the new queries will take effect immediately.
        /// </summary>
        public RelationalStorageProviderQueries CurrentOperationalQueries { get; set; }

        /// <summary> Logger used by this storage provider instance. </summary>
        /// <see cref="IStorageProvider.Log"/>
        public Logger Log { get; private set; }

        /// <summary> Name of this storage provider instance. </summary>
        /// <see cref="IProvider.Name"/>
        public string Name { get; private set; }

        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider.Init"/>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            if(string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("The parameter must contain characters", nameof(name));
            }

            if(providerRuntime == null)
            {
                throw new ArgumentNullException(nameof(providerRuntime));
            }

            if(config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if(!config.Properties.ContainsKey(DataConnectionStringPropertyName))
            {
                throw new BadProviderConfigException($"The {DataConnectionStringPropertyName} setting has not been configured. Add a {DataConnectionStringPropertyName} setting with a valid connection string.");
            }

            //NOTE: StorageSerializationPicker should be defined outside and given as a parameter in constructor or via Init in IProviderConfiguration perhaps.
            //Currently this limits one's options to much to the current situation of providing only one serializer for serialization and deserialization
            //with no regard to state update or serializer changes. Maybe have this serialized as a JSON in props and read via a key?
            StorageSerializationPicker = new DefaultRelationalStoragePicker(ConfigureDeserializers(config), ConfigureSerializers(config));

            //NOTE: Currently there should be only one pair of providers given. That is, only UseJsonFormatPropertyName, UseXmlFormatPropertyName or UseBinaryFormatPropertyName.
            if(StorageSerializationPicker.Deserializers.Count > 1 || StorageSerializationPicker.Serializers.Count > 1)
            {
                throw new ArgumentException("Configuration error, only one serializer and deserializer should be given.", nameof(config));
            }

            if(StorageSerializationPicker.Deserializers.Count == 0 || StorageSerializationPicker.Serializers.Count == 0)
            {
                StorageSerializationPicker.Deserializers.Add(new OrleansStorageDefaultBinaryDeserializer(UseBinaryFormatPropertyName));
                StorageSerializationPicker.Serializers.Add(new OrleansStorageDefaultBinarySerializer(UseBinaryFormatPropertyName));
            }

            var connectionInvariant = config.Properties.ContainsKey(DataConnectionInvariantPropertyName) ? config.Properties[DataConnectionInvariantPropertyName] : DefaultAdoInvariantInvariantPropertyName;
            Storage = RelationalStorage.CreateInstance(connectionInvariant, config.Properties[DataConnectionStringPropertyName]);
            ServiceId = providerRuntime.ServiceId.ToString();

            var queries = await Storage.ReadAsync(DefaultInitializationQuery, command => { }, (selector, resultSetCount, token) =>
            {
                return Task.FromResult(Tuple.Create(selector.GetValue<string>("QueryKey"), selector.GetValue<string>("QueryText")));
            }).ConfigureAwait(false);

            CurrentOperationalQueries = new RelationalStorageProviderQueries(
                queries.Single(i => i.Item1 == "WriteToStorageKey").Item2,
                queries.Single(i => i.Item1 == "ReadFromStorageKey").Item2,
                queries.Single(i => i.Item1 == "ReadFromStorageKey").Item2);

            Log = providerRuntime.GetLogger(GetType().FullName);
            Name = name;

            Log.Info((int)RelationalStorageProviderCodes.RelationalProviderInitProvider, $"Initialized storage provider: ServiceId={ServiceId} ProviderName={Name} Invariant={Storage.InvariantName} ConnectionString={Storage.ConnectionString}.");
        }


        /// <summary>
        /// <see cref="IProvider.Close"/>
        /// </summary>
        public Task Close()
        {
            return TaskDone.Done;
        }


        /// <summary>Clear state data function for this storage provider.</summary>
        /// <see cref="IStorageProvider.ClearStateAsync(string, GrainReference, IGrainState)"/>.
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown,
            //even if not as clear as when using explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var normalizedGrainType = ExtractClassBaseType(grainType);
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderClearing, $"Clearing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }

            string storageVersion = null;
            try
            {
                var clearRecord = (await Storage.ReadAsync(CurrentOperationalQueries.ClearState, command =>
                {
                    command.AddParameter("GrainIdHash", grainId.GetHashCode());
                    command.AddParameter("GrainIdString", grainId);
                    command.AddParameter("GrainTypeHash", normalizedGrainType.GetHashCode());
                    command.AddParameter("GrainTypeString", normalizedGrainType);
                    command.AddParameter("ServiceId", ServiceId);
                    command.AddParameter("GrainStateVersion", !string.IsNullOrWhiteSpace(grainState.ETag) ? int.Parse(grainState.ETag, CultureInfo.InvariantCulture) : default(int?));
                }, (selector, resultSetCount, token) => { return Task.FromResult(selector.GetValue(0).ToString()); }, CancellationToken.None).ConfigureAwait(false));
                storageVersion = clearRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                Log.Error((int)RelationalStorageProviderCodes.RelationalProviderDeleteError, $"Error clearing grain date: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag} Exception={ex.Message}.", ex);
                throw;
            }

            //If these are the same, it means no row was cleared in the storage.
            //Effectively it means the UPDATE or INSERT conditions failed due to ETag violation.
            //
            //NOTE: the storage could return also the new and old ETag (Version), but currently it doesn't.
            if(storageVersion == grainState.ETag)
            {
                //NOTE: this error message should be canonical across back-ends.
                throw new InconsistentStateException($"Version conflict when clearing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }

            //No errors found, the version of the state held by the grain can be updated.
            grainState.ETag = storageVersion;
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderCleared, $"Cleared grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }
        }


        /// <summary> Read state data function for this storage provider.</summary>
        /// <see cref="IStorageProvider.ReadStateAsync(string, GrainReference, IGrainState)"/>.
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var normalizedGrainType = ExtractClassBaseType(grainType);
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderReading, $"Reading grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }

            try
            {
                var readRecords = (await Storage.ReadAsync(CurrentOperationalQueries.ReadFromStorage, command =>
                {
                    command.AddParameter("GrainIdHash", grainId.GetHashCode());
                    command.AddParameter("GrainIdString", grainId);
                    command.AddParameter("GrainTypeHash", normalizedGrainType.GetHashCode());
                    command.AddParameter("GrainTypeString", normalizedGrainType);
                    command.AddParameter("ServiceId", ServiceId);
                }, (selector, resultSetCount, token) =>
                {
                    var version = selector.GetValue<int?>("Version");

                    //All but one of these should be null. All will be read and an appropriate deserializer picked.
                    //NOTE: When streaming will be implemented, it is worthwhile to optimize this so that the defined
                    //serializer will be picked and then streaming tried according to its tag.
                    var payloadBinary = selector.GetValueOrDefault<byte[]>("PayloadBinary");
                    var payloadJson = selector.GetValueOrDefault<string>("PayloadJson");
                    var payloadXml = selector.GetValueOrDefault<string>("PayloadXml");

                    object payload = payloadBinary;
                    string serializerKey = UseBinaryFormatPropertyName;
                    if(payload == null && payloadJson != null)
                    {
                        payload = payloadJson;
                        serializerKey = UseJsonFormatPropertyName;
                    }

                    if(payload == null && payloadXml != null)
                    {
                        payload = payloadXml;
                        serializerKey = UseXmlFormatPropertyName;
                    }

                    return Task.FromResult(Tuple.Create(payload, serializerKey, version));
                }, CancellationToken.None).ConfigureAwait(false)).ToArray();

                if(readRecords.Length > 0)
                {
                    var grainStateType = grainState.State.GetType();
                    var serializerKey = readRecords[0].Item2;
                    IStorageDeserializer deserializer = StorageSerializationPicker.PickDeserializer(ServiceId, Name, normalizedGrainType, grainReference, grainState, serializerKey);
                    object state = null;
                    if(deserializer != null)
                    {
                        state = readRecords[0].Item1 != null ? deserializer.Deserialize(readRecords[0].Item1, grainStateType) : null;
                    }
                    else
                    {
                        Log.Error((int)RelationalStorageProviderCodes.RelationalProviderNoDeserializer, $"No deserializer found for: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} Tag={serializerKey}.");
                    }

                    if(state == null)
                    {
                        Log.Info((int)RelationalStorageProviderCodes.RelationalProviderNoStateFound, $"Null grain state read (default will be instantiated): ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
                        state = Activator.CreateInstance(grainStateType);
                    }

                    var version = readRecords[0].Item3?.ToString(CultureInfo.InvariantCulture);
                    grainState.State = state;
                    grainState.ETag = version;

                    if(Log.IsVerbose3)
                    {
                        Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderRead, $"Read grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
                    }
                }
            }
            catch(Exception ex)
            {
                Log.Error((int)RelationalStorageProviderCodes.RelationalProviderReadError, $"Error reading grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag} Exception={ex.Message}.", ex);
                throw;
            }
        }


        /// <summary> Write state data function for this storage provider.</summary>
        /// <see cref="IStorageProvider.WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var data = grainState.State;
            var grainId = GrainIdAndExtensionAsString(grainReference);
            var normalizedGrainType = ExtractClassBaseType(grainType);
            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderWriting, $"Writing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }

            string storageVersion = null;
            try
            {

                var writeRecord = await Storage.ReadAsync(CurrentOperationalQueries.WriteToStorage, command =>
                {
                    command.AddParameter("GrainIdHash", grainId.GetHashCode());
                    command.AddParameter("GrainIdString", grainId);
                    command.AddParameter("GrainTypeHash", normalizedGrainType.GetHashCode());
                    command.AddParameter("GrainTypeString", normalizedGrainType);
                    command.AddParameter("ServiceId", ServiceId);
                    command.AddParameter("GrainStateVersion", !string.IsNullOrWhiteSpace(grainState.ETag) ? int.Parse(grainState.ETag, CultureInfo.InvariantCulture) : default(int?));

                    IStorageSerializer serializer = StorageSerializationPicker.PickSerializer(ServiceId, Name, normalizedGrainType, grainReference, grainState);
                    command.AddParameter("PayloadBinary", (byte[])(serializer.Tag == UseBinaryFormatPropertyName ? serializer.Serialize(data) : null));
                    command.AddParameter("PayloadJson", (string)(serializer.Tag == UseJsonFormatPropertyName ? serializer.Serialize(data) : null));
                    command.AddParameter("PayloadXml", (string)(serializer.Tag == UseXmlFormatPropertyName ? serializer.Serialize(data) : null));
                }, (selector, resultSetCount, token) => { return Task.FromResult(selector.GetValue(0).ToString()); }, CancellationToken.None).ConfigureAwait(false);
                storageVersion = writeRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                Log.Error((int)RelationalStorageProviderCodes.RelationalProviderWriteError, $"Error writing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag} Exception={ex.Message}.", ex);
                throw;
            }

            //If these are the same, it means no row was inserted or updated in the storage.
            //Effectively it means the UPDATE or INSERT conditions failed due to ETag violation.
            //
            //NOTE: the storage could return also the new and old ETag (Version), but currently it doesn't.
            if(storageVersion == grainState.ETag)
            {
                //TODO: Note that this error message should be canonical across back-ends.
                throw new InconsistentStateException($"Version conflict: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }

            //No errors found, the version of the state held by the grain can be updated.
            grainState.ETag = storageVersion;

            if(Log.IsVerbose3)
            {
                Log.Verbose3((int)RelationalStorageProviderCodes.RelationalProviderWrote, $"Wrote grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={normalizedGrainType} GrainId={grainId} ETag={grainState.ETag}.");
            }
        }


        /// <summary>
        /// Extracts the class base type without generic type information.
        /// </summary>
        /// <param name="classType">The class type information.</param>
        /// <returns>The base type without generic type information.</returns>
        /// <remarks>This is a work-around as Orleans gives grain type information in <see cref="ReadStateAsync(string, GrainReference, IGrainState)"/>
        /// without generic type information and in <see cref="WriteStateAsync(string, GrainReference, IGrainState)"/> with it. This can be removed
        /// when this is normalized.</remarks>
        private static string ExtractClassBaseType(string classType)
        {
            //According to the type info format at https://msdn.microsoft.com/en-us/library/w3f99sx1.aspx,
            //the generic information will always start with a back tick.
            int indexOfGenericTypeInformation = classType.IndexOf('`');
            if(indexOfGenericTypeInformation > -1)
            {
                return classType.Substring(0, indexOfGenericTypeInformation);
            }

            return classType;
        }


        /// <summary>
        /// Extracts a grain ID as a string and appends the key extension with '#' infix is present.
        /// </summary>
        /// <param name="grainReference">The reference from which to extract the ID.</param>
        /// <returns>The grain ID as a string.</returns>
        /// <remarks>This likely should exist in Orleans core in more optimized form.</remarks>
        private static string GrainIdAndExtensionAsString(GrainReference grainReference)
        {
            //The order is like this to have the actual type as string since:
            //1. If the key is a string one, it is returned, else null.
            //2. If the key is an integer one, it is returned. Else throw, unless see 3.
            //3. If the key is a GUID, it is returned, or Guid.Empty.
            //Also, 0 is returned for integer key when the actual type is Guid with the value Guid.Empty,
            //which makes 0 and Guid.Empty indistinguishable in the storage between these two types when
            //the value is the sole information.
            string primaryKey = null;
            string keyExtension = null;
            primaryKey = grainReference.GetPrimaryKeyString();
            if(primaryKey == null)
            {
                try { primaryKey = grainReference.GetPrimaryKeyLong(out keyExtension).ToString(CultureInfo.InvariantCulture); }
                catch
                {
                    //This exception comes if the grain reference primary key is not of integer type.
                    //Unfortunately it seem to be there is currently no other way to get this information.
                }

                //If the primary key is still null, it must be a GUID one.
                if(primaryKey == null)
                {
                    primaryKey = grainReference.GetPrimaryKey(out keyExtension).ToString();
                }
            }

            const string GrainIdAndExtensionSeparator = "#";
            return string.Format($"{primaryKey} {(keyExtension != null ? GrainIdAndExtensionSeparator + keyExtension : string.Empty)}");
        }


        private static ICollection<IStorageDeserializer> ConfigureDeserializers(IProviderConfiguration config)
        {
            const string @true = "true";
            var deserializers = new List<IStorageDeserializer>();
            if(config.Properties.ContainsKey(UseJsonFormatPropertyName) && @true.Equals(config.Properties[UseJsonFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                var jsonSettings = SerializationManager.UpdateSerializerSettings(SerializationManager.GetDefaultJsonSerializerSettings(), config);
                deserializers.Add(new OrleansStorageDefaultJsonDeserializer(jsonSettings, UseJsonFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseXmlFormatPropertyName) && @true.Equals(config.Properties[UseXmlFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                deserializers.Add(new OrleansStorageDefaultXmlDeserializer(UseXmlFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseBinaryFormatPropertyName) && @true.Equals(config.Properties[UseBinaryFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                deserializers.Add(new OrleansStorageDefaultBinaryDeserializer(UseBinaryFormatPropertyName));
            }

            return deserializers;
        }


        private static ICollection<IStorageSerializer> ConfigureSerializers(IProviderConfiguration config)
        {
            const string @true = "true";
            var serializers = new List<IStorageSerializer>();
            if(config.Properties.ContainsKey(UseJsonFormatPropertyName) && @true.Equals(config.Properties[UseJsonFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                var jsonSettings = SerializationManager.UpdateSerializerSettings(SerializationManager.GetDefaultJsonSerializerSettings(), config);
                serializers.Add(new OrleansStorageDefaultJsonSerializer(jsonSettings, UseJsonFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseXmlFormatPropertyName) && @true.Equals(config.Properties[UseXmlFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                serializers.Add(new OrleansStorageDefaultXmlSerializer(UseXmlFormatPropertyName));
            }

            if(config.Properties.ContainsKey(UseBinaryFormatPropertyName) && @true.Equals(config.Properties[UseBinaryFormatPropertyName], StringComparison.OrdinalIgnoreCase))
            {
                serializers.Add(new OrleansStorageDefaultBinarySerializer(UseBinaryFormatPropertyName));
            }

            return serializers;
        }
    }
}