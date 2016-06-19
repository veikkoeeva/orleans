using Newtonsoft.Json;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.SqlUtils;
using Orleans.Storage;
using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrleansSQLUtils.Storage.Provider
{
    public struct ReadStorageRecord
    {
        public byte[] PayloadBinary { get; set; }

        public string PayloadXml { get; set; }

        public string PayloadJson { get; set; }

        public int Version { get; set; }
    }


    public class RelationalStorageProvider: IStorageProvider
    {
        //The queries are currently hardcoded here as they aren't read from the DB.

        private const string WriteToStorageKey = @"
        -- When Orleans is running in normal, non-split state, there will
        -- be only one grain with the given ID and type combination only. This
        -- grain saves states mostly serially if Orleans guarantees are upheld. Even
        -- if not, the updates should work correctly due to version number.
        --
        -- In split brain situations there can be a situation where there are two or more
        -- grains with the given ID and type combination. When they try to INSERT
        -- concurrently, the table needs to be locked pessimistically before one of
        -- the grains gets @GrainStateVersion = 1 in return and the other grains will fail
        -- to update storage. The following arrangement is made to reduce locking in normal operation.
        --
        -- If the version number explicitly returned is still the same, Orleans interprets it so the update did not succeed
        -- and throws an InconsistentStateException.
        --
        -- See further information at See further at http://dotnet.github.io/orleans/Getting-Started-With-Orleans/Grain-Persistence.
        BEGIN TRANSACTION;
        SET XACT_ABORT, NOCOUNT ON;

        DECLARE @NewGrainStateVersion AS INT = @GrainStateVersion;

        -- If the @GrainStateVersion is not zero, this branch assumes it exists in this database.
        -- The NULL value is supplied by Orleans when the state is new.
        IF @GrainStateVersion IS NOT NULL
        BEGIN
            UPDATE Storage
            SET
	            PayloadBinary = @PayloadBinary,
	            PayloadJson = @PayloadJson,
	            PayloadXml = @PayloadXml,
	            ModifiedOn = GETUTCDATE(),
	            Version = Version + 1,
                @NewGrainStateVersion = Version + 1
            WHERE
	            GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
                AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
                AND GrainIdString = @GrainIdString AND @GrainIdString IS NOT NULL
                AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
		        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
                OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));
        END

        -- If no rows were updated, the reason could be that a new storage may have
        -- been brought online or it has been reset somehow while the silo is running
        -- having the grains in memory. Let's try to insert them anyhow as it is more
        -- more robust.
        IF @@ROWCOUNT = 0
        BEGIN
            INSERT INTO Storage
            (
	            GrainIdHash,
	            GrainIdString,
	            GrainTypeHash,
	            GrainTypeString,
		        ServiceId,
	            PayloadBinary,
	            PayloadJson,
	            PayloadXml,
	            ModifiedOn,
	            Version
            )
            SELECT
	            @GrainIdHash,
	            @GrainIdString,
	            @GrainTypeHash,
	            @GrainTypeString,
		        @ServiceId,
	            @PayloadBinary,
	            @PayloadJson,
	            @PayloadXml,
	            GETUTCDATE(),
	            1
             WHERE NOT EXISTS
             (
                SELECT 1
                FROM Storage WITH(UPDLOCK, HOLDLOCK)
                WHERE
                    GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
                    AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
                    AND GrainIdString = @GrainIdString AND @GrainIdString IS NOT NULL
                    AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
			        AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
                    AND Version IS NOT NULL
             ) OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));

            SET @NewGrainStateVersion = @@ROWCOUNT;
        END

        SELECT @NewGrainStateVersion;
        COMMIT TRANSACTION;";

        private const string ReadFromStorageKey = @"
        -- The application code will deserialize the relevant result. Not that the query optimizer
        -- estimates the result of rows based on its knowledge on the index. It doesn't know there
        -- will be only one row returned. Forcing the optimizer to process the first found row quickly
        -- creates an estimate for a one-row result and makes a difference on multi-million row tables.
        -- Also the optimizer is instructed to always use the same plan via index using the OPTIMIZE
        -- FOR UNKNOWN flags. These hints are only available in SQL Server 2008 and later. They
        -- should guarantee the execution time is robustly basically the same from query-to-query.
        SELECT
            PayloadBinary,
            PayloadXml,
            PayloadJson,
            Version
        FROM
            Storage
        WHERE
            GrainIdHash = @GrainIdHash
            AND GrainTypeHash = @GrainTypeHash
            AND GrainIdString = @GrainIdString
            AND GrainTypeString = @GrainTypeString
            OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));";

        private const string ClearQuery = @"
        BEGIN TRANSACTION;
        SET XACT_ABORT, NOCOUNT ON;
        DECLARE @NewGrainStateVersion AS INT = @GrainStateVersion;
        UPDATE Storage
        SET
	        PayloadBinary = NULL,
	        PayloadJson = NULL,
	        PayloadXml = NULL,
	        ModifiedOn = GETUTCDATE(),
	        Version = Version + 1,
            @NewGrainStateVersion = Version + 1
        WHERE
	        GrainIdHash = @GrainIdHash AND @GrainIdHash IS NOT NULL
            AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
            AND GrainIdString = @GrainIdString AND @GrainIdString IS NOT NULL
            AND GrainTypeString = @GrainTypeString AND @GrainTypeString IS NOT NULL
		    AND ServiceId = @ServiceId AND @ServiceId IS NOT NULL
            AND Version IS NOT NULL AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
            OPTION(FAST 1, OPTIMIZE FOR(@GrainIdHash UNKNOWN, @GrainTypeHash UNKNOWN));

        SELECT @NewGrainStateVersion;
        COMMIT TRANSACTION;";

        private const string DefaultAdoInvariantInvariantPropertyName = "System.Data.SqlClient";
        private const string DataConnectionStringPropertyName = "DataConnectionString";
        private const string DataConnectionInvariantPropertyName = "DataConnectionInvariantPropertyName";


        private JsonSerializerSettings JsonSettings { get; } = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
            PreserveReferencesHandling = PreserveReferencesHandling.None,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
        };

        private CommandBehavior CommandBehaviorOptimizations { get; } = CommandBehavior.SingleRow | CommandBehavior.SingleResult;

        private IRelationalStorage Storage { get; set; }

        private string ServiceId { get; set; }

        public Logger Log { get; private set; }

        public string Name { get; private set; }


        /// <summary>
        ///
        /// </summary>
        /// <param name="name"></param>
        /// <param name="providerRuntime"></param>
        /// <param name="config"></param>
        /// <returns></returns>
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
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
                throw new BadProviderConfigException($"The {DataConnectionStringPropertyName} setting has not been configured. Please add a {DataConnectionStringPropertyName} setting with a valid connection string.");
            }

            var connectionInvariant = config.Properties.ContainsKey(DataConnectionInvariantPropertyName) ? config.Properties[DataConnectionInvariantPropertyName] : DefaultAdoInvariantInvariantPropertyName;
            Storage = RelationalStorage.CreateInstance(connectionInvariant, config.Properties[DataConnectionStringPropertyName]);
            ServiceId = providerRuntime.ServiceId.ToString();

            Log = providerRuntime.GetLogger(GetType().FullName);
            Name = name;

            Log.Info($"Initialized storage provider: ServiceId={ServiceId} ProviderName={Name} Invariant={Storage.InvariantName} ConnectionString={Storage.ConnectionString}.");

            return TaskDone.Done;
        }


        public Task Close()
        {
            return TaskDone.Done;
        }


        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            if(Log.IsVerbose3)
            {
                const int ReadingDataCode = 1;
                Log.Verbose3(ReadingDataCode, $"Clearing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}");
            }

            string storageVersion = null;
            try
            {
                var clearRecord = (await Storage.ReadAsync(ClearQuery, command =>
                {
                    command.AddParameter("GrainIdHash", grainId.GetHashCode());
                    command.AddParameter("GrainIdString", grainId);
                    command.AddParameter("GrainTypeHash", grainType.GetHashCode());
                    command.AddParameter("GrainTypeString", grainType);
                    command.AddParameter("ServiceId", ServiceId);
                    command.AddParameter("GrainStateVersion", grainState.ETag);
                }, (selector, resultSetCount, token) => { return Task.FromResult(selector.GetValue(0).ToString()); }, CancellationToken.None, CommandBehaviorOptimizations).ConfigureAwait(false));
                storageVersion = clearRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                //Should this be the same as in ATS? Now this is bogus, not final...
                const int StorageProviderWriteErrorCode = -1;
                Log.Error(StorageProviderWriteErrorCode, string.Format($"Error clearing grain date: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag} Exception={ex.Message}", ex));
                throw;
            }

            //If these are the same, it means no row was cleared in the storage.
            //Effectively it means the UPDATE or INSERT conditions failed due to ETag violation.
            //NOTE: the storage could return also the new and old ETag (Version), but currently it doesn't.
            if(storageVersion == grainState.ETag)
            {
                //TODO: Note that this error message should be canonical across backends.
                throw new InconsistentStateException(string.Format($"Version conflict when clearing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}."));
            }

            //No errors found, the version of the state held by the grain can be updated.
            grainState.ETag = storageVersion;
        }


        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var grainId = GrainIdAndExtensionAsString(grainReference);
            if(Log.IsVerbose3)
            {
                const int ReadingDataCode = 1;
                Log.Verbose3(ReadingDataCode, $"Reading grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}");
            }

            try
            {
                var readRecords = (await Storage.ReadAsync(ReadFromStorageKey, command =>
                {
                    command.AddParameter("GrainIdHash", grainId.GetHashCode());
                    command.AddParameter("GrainIdString", grainId);
                    command.AddParameter("GrainTypeHash", grainType.GetHashCode());
                    command.AddParameter("GrainTypeString", grainType);
                    command.AddParameter("ServiceId", ServiceId);
                }, (selector, resultSetCount, token) =>
                {
                    return Task.FromResult(new ReadStorageRecord
                    {
                        PayloadBinary = selector.GetValueOrDefault<byte[]>("PayloadBinary"),
                        PayloadJson = selector.GetValueOrDefault<string>("PayloadJson"),
                        PayloadXml = selector.GetValueOrDefault<string>("PayloadXml"),
                        Version = selector.GetValue<int>("Version")
                    });
                }, CancellationToken.None, CommandBehaviorOptimizations).ConfigureAwait(false)).ToArray();

                //TODO: Currently hardcoded to JSON.
                if(readRecords.Length > 0)
                {
                    object state = null;
                    if(readRecords[0].PayloadBinary != null)
                    {
                        state = null;
                    }

                    if(readRecords[0].PayloadJson != null)
                    {
                        state = JsonConvert.DeserializeObject(readRecords[0].PayloadJson, grainState.State.GetType(), JsonSettings);
                    }

                    if(readRecords[0].PayloadXml != null)
                    {
                        state = null;
                    }

                    grainState.State = state ?? Activator.CreateInstance(grainState.State.GetType());
                    grainState.ETag = readRecords[0].Version.ToString();
                }
            }
            catch(Exception ex)
            {
                //Should this be the same as in ATS? Now this is bogus, not final...
                const int StorageProviderWriteErrorCode = -1;
                Log.Error(StorageProviderWriteErrorCode, string.Format($"Error reading grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag} Exception={ex.Message}", ex));
                throw;
            }
        }


        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            //It assumed these parameters are always valid. If not, an exception will be thrown, even if not as clear
            //as with explicitly checked parameters.
            var data = grainState.State;
            var grainId = GrainIdAndExtensionAsString(grainReference);

            if(Log.IsVerbose3)
            {
                const int ReadingDataCode = 1;
                Log.Verbose3(ReadingDataCode, $"Writing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}");
            }

            //TODO: Currently hardcoded to JSON.
            string jsonpayload = JsonConvert.SerializeObject(data, JsonSettings);
            string storageVersion = null;
            try
            {
                var writeRecord = await Storage.ReadAsync(WriteToStorageKey, command =>
                {
                    command.AddParameter("GrainIdHash", grainId.GetHashCode());
                    command.AddParameter("GrainIdString", grainId);
                    command.AddParameter("GrainTypeHash", grainType.GetHashCode());
                    command.AddParameter("GrainTypeString", grainType);
                    command.AddParameter("ServiceId", ServiceId);
                    command.AddParameter("GrainStateVersion", grainState.ETag);
                    command.AddParameter<byte[]>("PayloadBinary", null);
                    command.AddParameter("PayloadJson", jsonpayload);
                    command.AddParameter<string>("PayloadXml", null);
                }, (selector, resultSetCount, token) => { return Task.FromResult(selector.GetValue(0).ToString()); }, CancellationToken.None, CommandBehaviorOptimizations).ConfigureAwait(false);
                storageVersion = writeRecord.SingleOrDefault();
            }
            catch(Exception ex)
            {
                //Should this be the same as in ATS? Now this is bogus, not final...
                const int StorageProviderWriteErrorCode = -1;
                Log.Error(StorageProviderWriteErrorCode, string.Format($"Error writing grain state: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag} Exception={ex.Message}", ex));
                throw;
            }

            //If these are the same, it means no row was inserted or updated in the storage.
            //Effectively it means the UPDATE or INSERT conditions failed due to ETag violation.
            //NOTE: the storage could return also the new and old ETag (Version), but currently it doesn't.
            if(storageVersion == grainState.ETag)
            {
                //TODO: Note that this error message should be canonical across backends.
                throw new InconsistentStateException(string.Format($"Version conflict: ServiceId={ServiceId} ProviderName={Name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}."));
            }

            //No errors found, the version of the state held by the grain can be updated.
            grainState.ETag = storageVersion;
        }


        private static string GrainIdAndExtensionAsString(GrainReference grainReference)
        {
            //The order is like this since
            //1. If the key is a string one, it will return it, else null.
            //2. If the key is an integer one, it will be return. Else throw, unless see 3.
            //3. If the key is a GUID, it will return it, or Guid.Empty. Also 0 will be returned when asked for integer key, but it is a GUID one with Guid.Empty.
            string primaryKey = null;
            string keyExtension = null;
            primaryKey = grainReference.GetPrimaryKeyString();
            if(primaryKey == null)
            {
                try { primaryKey = grainReference.GetPrimaryKeyLong(out keyExtension).ToString(); }
                catch
                {
                    //This exception comes if the grain reference primary key is not of integer type.
                    //Unfortunately it seem to be there is currently no other way to get this information.
                }
            }

            if(primaryKey == null)
            {
                primaryKey = grainReference.GetPrimaryKey(out keyExtension).ToString();
            }

            const string GrainIdAndExtensionSeparator = "#";
            return string.Format($"{primaryKey} {(keyExtension != null ? GrainIdAndExtensionSeparator + keyExtension : string.Empty)}");
        }
    }
}
