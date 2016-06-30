/*
Implementation notes:

1) The general idea is that data is read and written through Orleans specific queries.
   Orleans operates on column names and types when reading and on parameter names and types when writing.

2) The implementations *must* preserve input and output names and types. Orleans uses these parameters to reads query results by name and type.
   Vendor and deployment specific tuning is allowed and contributions are encouraged as long as the interface contract
   is maintained.

3) The implementation across vendor specific scripts *should* preserve the constraint names. This simplifies troubleshooting
   by virtue of uniform naming across concrete implementations.

5) ETag for Orleans is an opaque column that represents a unique version. The type of its actual implementation
   is not important as long as it represents a unique version. In this implementation we use integers for versioning

6) For the sake of being explicit and removing ambiguity, Orleans expects some queries to return either TRUE as >0 value
   or FALSE as =0 value. That is, affected rows or such does not matter. If an error is raised or an exception is thrown
   the query *must* ensure the entire transaction is rolled back and may either return FALSE or propagate the exception.
   Orleans handles exception as a failure and will retry.

7) The implementation follows the Extended Orleans membership protocol. For more information, see at:
		http://dotnet.github.io/orleans/Runtime-Implementation-Details/Runtime-Tables.html
		http://dotnet.github.io/orleans/Runtime-Implementation-Details/Cluster-Management
		https://github.com/dotnet/orleans/blob/master/src/Orleans/SystemTargetInterfaces/IMembershipTable.cs
*/
-- This table defines Orleans operational queries. Orleans uses these to manage its operations,
-- these are the only queries Orleans issues to the database.
-- These can be redefined (e.g. to provide non-destructive updates) provided the stated interface principles hold.
CREATE TABLE OrleansQuery
(
	QueryKey VARCHAR(64) NOT NULL,
	QueryText VARCHAR(8000) NOT NULL,

	CONSTRAINT OrleansQuery_Key PRIMARY KEY(QueryKey)
);

-- For each deployment, there will be only one (active) membership version table version column which will be updated periodically.
CREATE TABLE OrleansMembershipVersionTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Version INT NOT NULL DEFAULT 0,

	CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY(DeploymentId)
);

-- Every silo instance has a row in the membership table.
CREATE TABLE OrleansMembershipTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	Address VARCHAR(45) NOT NULL,
	Port INT NOT NULL,
	Generation INT NOT NULL,
	SiloName NVARCHAR(150) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	Status INT NOT NULL,
	ProxyPort INT NULL,
	SuspectTimes VARCHAR(8000) NULL,
	StartTime DATETIME NOT NULL,
	IAmAliveTime DATETIME NOT NULL,

	CONSTRAINT PK_MembershipTable_DeploymentId PRIMARY KEY(DeploymentId, Address, Port, Generation),
	CONSTRAINT FK_MembershipTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
);

-- Orleans Reminders table - http://dotnet.github.io/orleans/Advanced-Concepts/Timers-and-Reminders
CREATE TABLE OrleansRemindersTable
(
	ServiceId NVARCHAR(150) NOT NULL,
	GrainId VARCHAR(150) NOT NULL,
	ReminderName NVARCHAR(150) NOT NULL,
	StartTime DATETIME NOT NULL,
	Period INT NOT NULL,
	GrainHash INT NOT NULL,
	Version INT NOT NULL,

	CONSTRAINT PK_RemindersTable_ServiceId_GrainId_ReminderName PRIMARY KEY(ServiceId, GrainId, ReminderName)
);

CREATE TABLE OrleansStatisticsTable
(
	OrleansStatisticsTableId INT NOT NULL AUTO_INCREMENT,
	DeploymentId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Id NVARCHAR(250) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	Name NVARCHAR(150) NOT NULL,
	IsValueDelta BIT NOT NULL,
	StatValue NVARCHAR(1024) NOT NULL,
	Statistic NVARCHAR(512) NOT NULL,

	CONSTRAINT StatisticsTable_StatisticsTableId PRIMARY KEY(OrleansStatisticsTableId)
);

CREATE TABLE OrleansClientMetricsTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	ClientId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Address VARCHAR(45) NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	CpuUsage FLOAT NOT NULL,
	MemoryUsage BIGINT NOT NULL,
	SendQueueLength INT NOT NULL,
	ReceiveQueueLength INT NOT NULL,
	SentMessages BIGINT NOT NULL,
	ReceivedMessages BIGINT NOT NULL,
	ConnectedGatewayCount BIGINT NOT NULL,

	CONSTRAINT PK_ClientMetricsTable_DeploymentId_ClientId PRIMARY KEY (DeploymentId , ClientId)
);

CREATE TABLE OrleansSiloMetricsTable
(
	DeploymentId NVARCHAR(150) NOT NULL,
	SiloId NVARCHAR(150) NOT NULL,
	Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	Address VARCHAR(45) NOT NULL,
	Port INT NOT NULL,
	Generation INT NOT NULL,
	HostName NVARCHAR(150) NOT NULL,
	GatewayAddress VARCHAR(45) NOT NULL,
	GatewayPort INT NOT NULL,
	CpuUsage FLOAT NOT NULL,
	MemoryUsage BIGINT NOT NULL,
	SendQueueLength INT NOT NULL,
	ReceiveQueueLength INT NOT NULL,
	SentMessages BIGINT NOT NULL,
	ReceivedMessages BIGINT NOT NULL,
	ActivationCount INT NOT NULL,
	RecentlyUsedActivationCount INT NOT NULL,
	RequestQueueLength BIGINT NOT NULL,
	IsOverloaded BIT NOT NULL,
	ClientCount BIGINT NOT NULL,

	CONSTRAINT PK_SiloMetricsTable_DeploymentId_SiloId PRIMARY KEY (DeploymentId , SiloId),
	CONSTRAINT FK_SiloMetricsTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
);


-- The design criteria for this table are:
--
-- 1. It can contain arbitrary content serialized as binary, XML or JSON. These formats
-- are supported to allow one to take advantage of in-storage processing capabilities for
-- these types if required. This should not incur extra cost on storage.
--
-- 2. The table design should scale with the idea of tens or hundreds (or even more) types
-- of grains that may operate with even hundreds of thousands of grain IDs within each
-- type of a grain.
--
-- 3. The table and its associated operations should remain stable. There should not be
-- structural reason for unexpected delays in operations. It should be possible to also
-- insert data reasonably fast without resource contention.
--
-- 4. For reasons in 2. and 3., the index should be as narrow as possible so it fits well in
-- memory and should it require maintenance, it should be non-resource intensive. For this
-- reason the index is narrow by design (ideally non-clustered). Currently the entity
-- is recognized in the storage by
-- the grain type and its ID, which are unique in Orleans silo. As adding two
-- NVARCHAR(150) fields into a index would make it more resource intensive, the values
-- are hashed into two INT type instance, which are made a compound index. When there are no
-- collisions, the index can quickly locate the unique row. Along with the hashed index
-- values, the NVARCHAR(150) values are also stored and they are used to prune hash
-- collisions down to only one result row.
--
-- 5. The design leads to duplication in the storage. It is reasonable to assume there will
-- a low number of services with a given service ID operational at any given time. Or that
-- compared to the number of grain IDs, there are a fairly low number of different types of
-- grain. The catch is that were these data separated to another table, it would make INSERT
-- and UPDATE operations complicated and would require joins, temporary variables and additional
-- indexes or some combinations of them to make it work. It looks like fitting strategy
-- could be to use table compression.
--
-- 6. For the aforementioned reasons, grain state DELETE will set NULL to the data fields
-- and updates the Version number normally. This should alleviate the need for index or
-- statistics maintenance with the loss of some bytes of storage space. The table can be scrubbed
-- in a separate maintenance operation.
CREATE TABLE Storage
(
    -- These are for the book keeping. Orleans calculates
    -- these hashes (Jenkins), which are unsigned 32 integers mapped to
    -- the *Hash fields. The mapping is done in the code. The
    -- *String columns contain the corresponding clear name fields.
	--
	-- If there are duplicates, they are resolved by using GrainIdString
	-- and GrainNameString fields. It is assumed these would be rarely needed.
    GrainIdHash		INT NOT NULL,
    GrainIdString   NVARCHAR(150) NOT NULL,
    GrainTypeHash   INT NOT NULL,
    GrainTypeString NVARCHAR(150) NOT NULL,
	ServiceId		NVARCHAR(150) NOT NULL,

    -- The usage of the Payload records is exclusive in that
    -- only one is populated at any given time and two others
    -- are NULL. When all three are returned, the application
    -- knows how to handle the situation. The advantage on separating
	-- these by types is that various DB engines include additional
	-- in-storage processing and "compression" capabilities depending on type.
	--
	-- One is free to alter the size of these fields.
    PayloadBinary	BLOB NULL,
    PayloadXml		TEXT NULL,
    PayloadJson		TEXT NULL,

    -- Informational field, no other use.
    ModifiedOn DATETIME NOT NULL,

    -- The version of this table.
    Version INT NULL,

    -- The following would in principle be the primary key, but it would be too thick
	-- to be indexed, so the the values are hashed and only collisions will be solved
	-- by using the fields. That is, after the indexed queries have pinpointed the right
	-- rows down to [0, n] relevant ones, n being the number of collided value pairs.
    -- CONSTRAINT PK_Storage PRIMARY KEY NONCLUSTERED (GrainIdString, GrainTypeString).
    -- This would be better as a non-clustered index, but the usual MySQL engines do not support it.
    CONSTRAINT IX_Storage PRIMARY KEY(GrainIdHash, GrainTypeHash)
) ROW_FORMAT = COMPRESSED;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateIAmAlivetimeKey','
	-- This is expected to never fail by Orleans, so return value
	-- is not needed nor is it checked.
	UPDATE OrleansMembershipTable
	SET
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertMembershipVersionKey','
	INSERT INTO OrleansMembershipVersionTable
	(
		DeploymentId
	)
	SELECT @DeploymentId
	WHERE NOT EXISTS
	(
	SELECT 1
	FROM
		OrleansMembershipVersionTable
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
	);

	SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertMembershipKey','
	call InsertMembershipKey(@DeploymentId, @Address, @Port, @Generation,
	@Version, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime);'
);

DELIMITER $$

CREATE PROCEDURE InsertMembershipKey(
	in	_DeploymentId NVARCHAR(150),
	in	_Address VARCHAR(45),
	in	_Port INT,
	in	_Generation INT,
	in	_Version INT,
	in	_SiloName NVARCHAR(150),
	in	_HostName NVARCHAR(150),
	in	_Status INT,
	in	_ProxyPort INT,
	in	_StartTime DATETIME,
	in	_IAmAliveTime DATETIME
)
BEGIN
	DECLARE _ROWCOUNT INT;
	START TRANSACTION;
	INSERT INTO OrleansMembershipTable
	(
		DeploymentId,
		Address,
		Port,
		Generation,
		SiloName,
		HostName,
		Status,
		ProxyPort,
		StartTime,
		IAmAliveTime
	)
	SELECT
		_DeploymentId,
		_Address,
		_Port,
		_Generation,
		_SiloName,
		_HostName,
		_Status,
		_ProxyPort,
		_StartTime,
		_IAmAliveTime
	WHERE NOT EXISTS
	(
	SELECT 1
	FROM
		OrleansMembershipTable
	WHERE
		DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
		AND Address = _Address AND _Address IS NOT NULL
		AND Port = _Port AND _Port IS NOT NULL
		AND Generation = _Generation AND _Generation IS NOT NULL
	);

	UPDATE OrleansMembershipVersionTable
	SET
		Version = Version + 1
	WHERE
		DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
		AND Version = _Version AND _Version IS NOT NULL
		AND ROW_COUNT() > 0;

	SET _ROWCOUNT = ROW_COUNT();

	IF _ROWCOUNT = 0
	THEN
		ROLLBACK;
	ELSE
		COMMIT;
	END IF;
	SELECT _ROWCOUNT;
END$$

DELIMITER ;

CREATE PROCEDURE ClearStorage
(
	in _GrainIdHash INT,
    in _GrainIdString NVARCHAR(150),
    in _GrainTypeHash INT,
    in _GrainTypeString NVARCHAR(150),
    in _ServiceId NVARCHAR(150),
    in _GrainStateVersion INT
)
BEGIN
	DECLARE _newGrainStateVersion INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION BEGIN ROLLBACK; RESIGNAL; END;
    DECLARE EXIT HANDLER FOR SQLWARNING BEGIN ROLLBACK; RESIGNAL; END;

    SET _newGrainStateVersion = _GrainStateVersion;

    START TRANSACTION;
    UPDATE Storage
    SET
	    PayloadBinary = NULL,
	    PayloadJson = NULL,
	    PayloadXml = NULL,
	    Version = Version + 1,
        _NewGrainStateVersion = Version + 1
    WHERE
	    GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
        AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
        AND GrainIdString = _GrainIdString AND _GrainIdString IS NOT NULL
        AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
		AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
        AND Version IS NOT NULL AND Version = _GrainStateVersion AND _GrainStateVersion IS NOT NULL
        LIMIT 1;

    SELECT _newGrainStateVersion;
    COMMIT;
END $$

DELIMITER ;


CREATE PROCEDURE WriteToStorage
(
	in _GrainIdHash INT,
    in _GrainIdString NVARCHAR(150),
    in _GrainTypeHash INT,
    in _GrainTypeString NVARCHAR(150),
    in _ServiceId NVARCHAR(150),
    in _GrainStateVersion INT,
    in _PayloadBinary BLOB,
    in _PayloadJson TEXT,
    in _PayloadXml TEXT
)
BEGIN
	DECLARE _newGrainStateVersion INT;
    DECLARE _rowCount INT;
	DECLARE EXIT HANDLER FOR SQLEXCEPTION BEGIN ROLLBACK; RESIGNAL; END;
    DECLARE EXIT HANDLER FOR SQLWARNING BEGIN ROLLBACK; RESIGNAL; END;

    SET _newGrainStateVersion = _GrainStateVersion;

    START TRANSACTION;
	IF _GrainStateVersion IS NOT NULL
	THEN
		UPDATE Storage
		SET
			PayloadBinary = _PayloadBinary,
			PayloadJson = _PayloadJson,
			PayloadXml = _PayloadXml,
			ModifiedOn = UTC_TIMESTAMP(),
            Version = Version + 1
		WHERE
			GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
			AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
			AND GrainIdString = _GrainIdString AND _GrainIdString IS NOT NULL
			AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
			AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
			AND Version IS NOT NULL AND Version = _GrainStateVersion AND _GrainStateVersion IS NOT NULL
			LIMIT 1;

        IF ROW_COUNT() > 0
        THEN
			SET _newGrainStateVersion = _GrainStateVersion + 1;
        END IF;
	END IF;

    IF _newGrainStateVersion IS NULL AND _GrainStateVersion IS NULL
	THEN
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
			_GrainIdHash,
			_GrainIdString,
			_GrainTypeHash,
			_GrainTypeString,
			_ServiceId,
			_PayloadBinary,
			_PayloadJson,
			_PayloadXml,
            UTC_TIMESTAMP(),
			1
		WHERE NOT EXISTS
		(
			SELECT 1
			FROM Storage
			WHERE
				GrainIdHash = _GrainIdHash AND _GrainIdHash IS NOT NULL
				AND GrainTypeHash = _GrainTypeHash AND _GrainTypeHash IS NOT NULL
				AND GrainIdString = _GrainIdString AND _GrainIdString IS NOT NULL
				AND GrainTypeString = _GrainTypeString AND _GrainTypeString IS NOT NULL
				AND ServiceId = _ServiceId AND _ServiceId IS NOT NULL
				AND Version = @GrainStateVersion AND @GrainStateVersion IS NOT NULL
		) LIMIT 1;

		SET _newGrainStateVersion = 1;
	END IF;

    SELECT _newGrainStateVersion AS NewGrainStateVersion;
    COMMIT;
END $$

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpdateMembershipKey','
	START TRANSACTION;

	UPDATE OrleansMembershipVersionTable
	SET
		Version = Version + 1
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL;

	UPDATE OrleansMembershipTable
	SET
		Status = @Status,
		SuspectTimes = @SuspectTimes,
		IAmAliveTime = @IAmAliveTime
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
		AND ROW_COUNT() > 0;

	SELECT ROW_COUNT();
	COMMIT;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReminderRowKey','
	INSERT INTO OrleansRemindersTable
	(
		ServiceId,
		GrainId,
		ReminderName,
		StartTime,
		Period,
		GrainHash,
		Version
	)
	VALUES
	(
		@ServiceId,
		@GrainId,
		@ReminderName,
		@StartTime,
		@Period,
		@GrainHash,
		last_insert_id(0)
	)
	ON DUPLICATE KEY
	UPDATE
		StartTime = @StartTime,
		Period = @Period,
		GrainHash = @GrainHash,
		Version = last_insert_id(Version+1);


	SELECT last_insert_id() AS Version;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertReportClientMetricsKey','
	INSERT INTO OrleansClientMetricsTable
	(
		DeploymentId,
		ClientId,
		Address,
		HostName,
		CpuUsage,
		MemoryUsage,
		SendQueueLength,
		ReceiveQueueLength,
		SentMessages,
		ReceivedMessages,
		ConnectedGatewayCount
	)
	VALUES
	(
		@DeploymentId,
		@ClientId,
		@Address,
		@HostName,
		@CpuUsage,
		@MemoryUsage,
		@SendQueueLength,
		@ReceiveQueueLength,
		@SentMessages,
		@ReceivedMessages,
		@ConnectedGatewayCount
	)
	ON DUPLICATE KEY
	UPDATE
		Address = @Address,
		HostName = @HostName,
		CpuUsage = @CpuUsage,
		MemoryUsage = @MemoryUsage,
		SendQueueLength = @SendQueueLength,
		ReceiveQueueLength = @ReceiveQueueLength,
		SentMessages = @SentMessages,
		ReceivedMessages = @ReceivedMessages,
		ConnectedGatewayCount = @ConnectedGatewayCount;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'UpsertSiloMetricsKey','
	INSERT INTO OrleansSiloMetricsTable
	(
		DeploymentId,
		SiloId,
		Address,
		Port,
		Generation,
		HostName,
		GatewayAddress,
		GatewayPort,
		CpuUsage,
		MemoryUsage,
		SendQueueLength,
		ReceiveQueueLength,
		SentMessages,
		ReceivedMessages,
		ActivationCount,
		RecentlyUsedActivationCount,
		RequestQueueLength,
		IsOverloaded,
		ClientCount
	)
	VALUES
	(
		@DeploymentId,
		@SiloId,
		@Address,
		@Port,
		@Generation,
		@HostName,
		@GatewayAddress,
		@GatewayPort,
		@CpuUsage,
		@MemoryUsage,
		@SendQueueLength,
		@ReceiveQueueLength,
		@SentMessages,
		@ReceivedMessages,
		@ActivationCount,
		@RecentlyUsedActivationCount,
		@RequestQueueLength,
		@IsOverloaded,
		@ClientCount
	)
	ON DUPLICATE KEY
	UPDATE
		Address = @Address,
		Port = @Port,
		Generation = @Generation,
		HostName = @HostName,
		GatewayAddress = @GatewayAddress,
		GatewayPort= @GatewayPort,
		CpuUsage = @CpuUsage,
		MemoryUsage = @MemoryUsage,
		SendQueueLength = @SendQueueLength,
		ReceiveQueueLength = @ReceiveQueueLength,
		SentMessages = @SentMessages,
		ReceivedMessages = @ReceivedMessages,
		ActivationCount = @ActivationCount,
		RecentlyUsedActivationCount = @RecentlyUsedActivationCount,
		RequestQueueLength = @RequestQueueLength,
		IsOverloaded = @IsOverloaded,
		ClientCount = @ClientCount;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'GatewaysQueryKey','
	SELECT
		Address,
		ProxyPort,
		Generation
	FROM
		OrleansMembershipTable
	WHERE
		DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
		AND Status = @Status AND @Status IS NOT NULL
		AND ProxyPort > 0;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadRowKey','
	SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		OrleansMembershipVersionTable v
		-- This ensures the version table will returned even if there is no matching membership row.
		LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
		AND Address = @Address AND @Address IS NOT NULL
		AND Port = @Port AND @Port IS NOT NULL
		AND Generation = @Generation AND @Generation IS NOT NULL
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'MembershipReadAllKey','
	SELECT
		v.DeploymentId,
		m.Address,
		m.Port,
		m.Generation,
		m.SiloName,
		m.HostName,
		m.Status,
		m.ProxyPort,
		m.SuspectTimes,
		m.StartTime,
		m.IAmAliveTime,
		v.Version
	FROM
		OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m
		ON v.DeploymentId = m.DeploymentId
	WHERE
		v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteMembershipTableEntriesKey','
	DELETE FROM OrleansMembershipTable
	WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
	DELETE FROM OrleansMembershipVersionTable
	WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowsKey','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadReminderRowKey','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL
		AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows1Key','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainHash > @BeginHash AND @BeginHash IS NOT NULL
		AND GrainHash <= @EndHash AND @EndHash IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadRangeRows2Key','
	SELECT
		GrainId,
		ReminderName,
		StartTime,
		Period,
		Version
	FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND ((GrainHash > @BeginHash AND @BeginHash IS NOT NULL)
		OR (GrainHash <= @EndHash AND @EndHash IS NOT NULL));
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'InsertOrleansStatisticsKey','
	START TRANSACTION;
	INSERT INTO OrleansStatisticsTable
	(
		DeploymentId,
		Id,
		HostName,
		Name,
		IsValueDelta,
		StatValue,
		Statistic
	)
	SELECT
		@DeploymentId,
		@Id,
		@HostName,
		@Name,
		@IsValueDelta,
		@StatValue,
		@Statistic;
	COMMIT;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowKey','
	DELETE FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL
		AND GrainId = @GrainId AND @GrainId IS NOT NULL
		AND ReminderName = @ReminderName AND @ReminderName IS NOT NULL
		AND Version = @Version AND @Version IS NOT NULL;
	SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'DeleteReminderRowsKey','
	DELETE FROM OrleansRemindersTable
	WHERE
		ServiceId = @ServiceId AND @ServiceId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ReadFromStorageKey',
	'SELECT
        PayloadBinary,
        PayloadXml,
        PayloadJson,
        UTC_TIMESTAMP(),
        Version
    FROM
        Storage
    WHERE
        GrainIdHash = @GrainIdHash
        AND GrainTypeHash = @GrainTypeHash AND @GrainTypeHash IS NOT NULL
        AND GrainIdString = @GrainIdString AND GrainIdString IS NOT NULL
        AND GrainTypeString = @GrainTypeString AND GrainTypeString IS NOT NULL
        LIMIT 1;'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'WriteToStorageKey','
	call WriteToStorage(@GrainIdHash, @GrainIdString, @GrainTypeHash, @GrainTypeString, @ServiceId, @GrainStateVersion, @PayloadBinary, @PayloadJson, @PayloadXml);'
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
	'ClearStorageKey','
	call ClearStorage(@GrainIdHash, @GrainIdString, @GrainTypeHash, @GrainTypeString, @ServiceId, @GrainStateVersion);'
);
