using System.Text.Json;
using Backend;
using Backend.Controllers;
using Backend.Data;
using Backend.Models;
using Backend.Services;
using Backend.Services.IntegrationDaemon;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Backend.Tests.Controllers;

public class ChargingStationsControllerTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly Mock<IJwtService> _jwtMock;
    private readonly Mock<IIntegrationDaemonClient> _daemonMock;

    public ChargingStationsControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new ApplicationDbContext(options);
        _jwtMock = new Mock<IJwtService>();
        _daemonMock = new Mock<IIntegrationDaemonClient>();
    }

    public void Dispose() => _db.Dispose();

    private ChargingStationsController BuildController(string? userId = "1")
    {
        _jwtMock.Setup(j => j.ValidateToken(It.IsAny<string>())).Returns(userId);
        var controller = new ChargingStationsController(
            _db,
            _jwtMock.Object,
            _daemonMock.Object,
            NullLogger<ChargingStationsController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer t";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public async Task GetAll_InvalidToken_ReturnsUnauthorized()
    {
        var result = await BuildController(userId: null).GetAll();
        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task GetAll_NonAdmin_ReturnsForbid()
    {
        _db.Users.Add(new User { Id = 1, Email = "u@test.com", Name = "U", GoogleId = "g1", Role = UserRole.Base });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetAll();
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetAll_Admin_GroupsSessionsBySerialNumber()
    {
        _db.Users.Add(new User { Id = 1, Email = "a@test.com", Name = "Admin", GoogleId = "g1", Role = UserRole.Admin });
        _db.ChargeCards.Add(new ChargeCard { Value = "RF-STATION-TEST" });
        await _db.SaveChangesAsync();
        var cid = _db.ChargeCards.Single().Id;

        var t1 = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
        var sidA = ChargingPointIds.ChargingPointIdFromSerialAndConnector("6531001272", "1");
        var sidB = ChargingPointIds.ChargingPointIdFromSerialAndConnector("6531001273", "1");
        _db.ChargingSessions.AddRange(
            TestData.Session(cid, sidA, t1, t1.AddHours(1), 5, "6531001272"),
            TestData.Session(cid, sidA, t2, t2.AddHours(1), 3, "6531001272"),
            TestData.Session(cid, sidB, t1, t1.AddHours(1), 2, "6531001273")
        );
        await _db.SaveChangesAsync();

        var result = await BuildController().GetAll();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var stations = doc.RootElement.GetProperty("stations");
        Assert.Equal(2, stations.GetArrayLength());
    }

    [Fact]
    public async Task GetAll_Admin_IncludesLegacyBucket()
    {
        _db.Users.Add(new User { Id = 1, Email = "a@test.com", Name = "Admin", GoogleId = "g1", Role = UserRole.Admin });
        _db.ChargeCards.Add(new ChargeCard { Value = "RF-LEGACY" });
        await _db.SaveChangesAsync();
        var cid = _db.ChargeCards.Single().Id;
        var t = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _db.ChargingSessions.Add(new ChargingSession
        {
            ChargeCardId = cid,
            StationId = 99,
            FromTimestamp = TestData.UtcMs(t),
            ToTimestamp = TestData.UtcMs(t.AddHours(1)),
            StartReading = 0,
            StopReading = 1m,
            SerialNumber = null,
            Rfid = "r",
            Status = "STOPPED"
        });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetAll();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.GetProperty("stations").EnumerateArray().First();
        Assert.True(first.GetProperty("isLegacy").GetBoolean());
        Assert.Equal(99, first.GetProperty("stationId").GetInt32());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_FiltersTypeAndCapsAtFive()
    {
        var now = DateTime.UtcNow;
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson =
                """[{"id":1,"name":"Paal A","type":"Charging station","sourceType":"CT"},{"id":2,"name":"PV","type":"Solar","sourceType":"x"}]"""
        });
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 2,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":3,"name":"Paal B","type":"Charging station","sourceType":"CT"}]"""
        });
        await _db.SaveChangesAsync();

        var c = BuildController();
        var result = await c.GetSmappeeChargingStationDisplayNames(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var squares = doc.RootElement.GetProperty("squares");
        Assert.Equal(2, squares.GetArrayLength());
        Assert.Equal("Paal A", squares[0].GetProperty("name").GetString());
        Assert.Equal("Paal B", squares[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_OrdersByNameCaseInsensitive()
    {
        var now = DateTime.UtcNow;
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 2,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":1,"name":"zebra","type":"Charging station","sourceType":"CT"}]"""
        });
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":2,"name":"Alpha","type":"Charging station","sourceType":"CT"}]"""
        });
        await _db.SaveChangesAsync();

        var c = BuildController();
        var result = await c.GetSmappeeChargingStationDisplayNames(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var squares = doc.RootElement.GetProperty("squares");
        Assert.Equal(2, squares.GetArrayLength());
        Assert.Equal("Alpha", squares[0].GetProperty("name").GetString());
        Assert.Equal("zebra", squares[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_UsesLiveSnapshotByNormalizedStationName()
    {
        var now = DateTime.UtcNow;
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":1,"name":"Paal 4","type":"Charging station","sourceType":"CT"}]"""
        });
        _db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now,
            IsSuccess = true,
            PayloadJson = """
                          {
                            "FetchedAt":"2026-04-27T14:00:00Z",
                            "ServiceLocationId":1,
                            "Points":[
                              {
                                "SerialNumber":"6531001274",
                                "StationName":"Paal 4 - 1",
                                "UiState":"charging"
                              }
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetSmappeeChargingStationDisplayNames(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var square = Assert.Single(doc.RootElement.GetProperty("squares").EnumerateArray());
        Assert.Equal("Paal 4", square.GetProperty("name").GetString());
        Assert.Equal(ChargingStationHomeSquareDto.LiveStateActive, square.GetProperty("liveState").GetString());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_PrefersActiveWhenDuplicateLiveNamesExist()
    {
        var now = DateTime.UtcNow;
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":1,"name":"Paal 4","type":"Charging station","sourceType":"CT"}]"""
        });
        _db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now,
            IsSuccess = true,
            PayloadJson = """
                          {
                            "FetchedAt":"2026-04-27T14:00:00Z",
                            "ServiceLocationId":1,
                            "Points":[
                              {
                                "SerialNumber":"6531001274",
                                "StationName":"Paal 4",
                                "UiState":"available"
                              },
                              {
                                "SerialNumber":"6531001274",
                                "StationName":"Paal 4 - 1",
                                "UiState":"charging"
                              }
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetSmappeeChargingStationDisplayNames(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var square = Assert.Single(doc.RootElement.GetProperty("squares").EnumerateArray());
        Assert.Equal(ChargingStationHomeSquareDto.LiveStateActive, square.GetProperty("liveState").GetString());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_InUseAndPowerKwAreReturned()
    {
        var now = DateTime.UtcNow;
        var started = now.AddHours(-2);
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":1,"name":"Paal 4","type":"Charging station","sourceType":"CT"}]"""
        });
        _db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now,
            IsSuccess = true,
            PayloadJson = $$"""
                          {
                            "FetchedAt":"{{now:O}}",
                            "ServiceLocationId":1,
                            "Points":[
                              {
                                "SerialNumber":"6531001274",
                                "StationName":"Paal 4",
                                "UiState":"charging",
                                "EnergyKwh":14,
                                "StartedAtUtc":"{{started:O}}"
                              }
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetSmappeeChargingStationDisplayNames(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var square = Assert.Single(doc.RootElement.GetProperty("squares").EnumerateArray());
        Assert.True(square.GetProperty("inUse").GetBoolean());
        Assert.Equal(7m, square.GetProperty("powerKw").GetDecimal());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_DeduplicatesByNameAndKeepsStrongestLiveState()
    {
        var now = DateTime.UtcNow;
        _db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson = """
                             [
                               {"id":1,"name":"Paal 4","type":"Charging station","sourceType":"CT"},
                               {"id":2,"name":"Paal 4 - 1","type":"Charging station","sourceType":"CT"}
                             ]
                             """
        });
        _db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now,
            IsSuccess = true,
            PayloadJson = """
                          {
                            "FetchedAt":"2026-04-27T14:00:00Z",
                            "ServiceLocationId":1,
                            "Points":[
                              {
                                "SerialNumber":"6531001274",
                                "StationName":"Paal 4",
                                "UiState":"charging",
                                "EnergyKwh":10,
                                "StartedAtUtc":"2026-04-27T12:00:00Z"
                              }
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetSmappeeChargingStationDisplayNames(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var squares = doc.RootElement.GetProperty("squares");
        Assert.Equal(1, squares.GetArrayLength());
        var square = squares[0];
        Assert.Equal("Paal 4", square.GetProperty("name").GetString());
        Assert.Equal(ChargingStationHomeSquareDto.LiveStateActive, square.GetProperty("liveState").GetString());
        Assert.True(square.GetProperty("inUse").GetBoolean());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_UsesHardcodedNameWhenSnapshotNameMissing()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(options);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.ValidateToken(It.IsAny<string>())).Returns("1");
        var daemon = new Mock<IIntegrationDaemonClient>();
        var controller = new ChargingStationsController(
            db,
            jwt.Object,
            daemon.Object,
            NullLogger<ChargingStationsController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer t";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var now = DateTime.UtcNow;
        db.SmappeeServiceLocations.Add(new SmappeeServiceLocation
        {
            ServiceLocationId = 1,
            UpdatedAtUtc = now,
            AppliancesJson = """[{"id":1,"name":"Paal 4 - 1","type":"Charging station","sourceType":"CT"}]"""
        });
        db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now,
            IsSuccess = true,
            PayloadJson = """
                          {
                            "FetchedAt":"2026-04-27T14:00:00Z",
                            "ServiceLocationId":1,
                            "Points":[
                              {
                                "SerialNumber":"5821063919",
                                "StationName":null,
                                "UiState":"charging",
                                "EnergyKwh":11.82,
                                "StartedAtUtc":"2026-04-27T13:22:59Z"
                              }
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        await db.SaveChangesAsync();

        var result = await controller.GetSmappeeChargingStationDisplayNames(default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var square = Assert.Single(doc.RootElement.GetProperty("squares").EnumerateArray());
        Assert.Equal("Paal 4", square.GetProperty("name").GetString());
        Assert.True(square.GetProperty("inUse").GetBoolean());
        Assert.Equal(ChargingStationHomeSquareDto.LiveStateActive, square.GetProperty("liveState").GetString());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_NoStoredDataReturnsEmptySquares()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(options);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.ValidateToken(It.IsAny<string>())).Returns("1");
        var daemon = new Mock<IIntegrationDaemonClient>();
        var controller = new ChargingStationsController(
            db,
            jwt.Object,
            daemon.Object,
            NullLogger<ChargingStationsController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer t";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.GetSmappeeChargingStationDisplayNames(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var squares = doc.RootElement.GetProperty("squares");
        Assert.Equal(0, squares.GetArrayLength());
    }

    [Fact]
    public async Task GetSmappeeChargingStationDisplayNames_UsesSessionSerialsWhenNoAppliancesStored()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new ApplicationDbContext(options);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.ValidateToken(It.IsAny<string>())).Returns("1");
        var daemon = new Mock<IIntegrationDaemonClient>();

        db.ChargeCards.Add(new ChargeCard { Value = "RF-1" });
        await db.SaveChangesAsync();
        var cardId = db.ChargeCards.Single().Id;
        var t = new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
        db.ChargingSessions.Add(TestData.Session(cardId, 1, t, t.AddHours(1), 3m, "5821063914"));
        db.ChargingSessions.Add(TestData.Session(cardId, 2, t, t.AddHours(1), 4m, "9999999999"));
        await db.SaveChangesAsync();

        var controller = new ChargingStationsController(
            db,
            jwt.Object,
            daemon.Object,
            NullLogger<ChargingStationsController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer t";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.GetSmappeeChargingStationDisplayNames(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);
        var squares = doc.RootElement.GetProperty("squares");
        Assert.Equal(2, squares.GetArrayLength());
        Assert.Equal("Paal 5", squares[0].GetProperty("name").GetString());
        Assert.Equal("Unknown 9999999999", squares[1].GetProperty("name").GetString());

        var serialNumbers = doc.RootElement.GetProperty("serialNumbers");
        Assert.Equal(2, serialNumbers.GetArrayLength());
        Assert.Equal("5821063914", serialNumbers[0].GetString());
        Assert.Equal("9999999999", serialNumbers[1].GetString());
    }

    [Fact]
    public async Task GetPublicDisplay_ShowsUserCodeOnlyWhileTheStationIsInUse()
    {
        var now = DateTime.UtcNow;
        _db.Users.Add(new User
        {
            Id = 7,
            Email = "driver@example.com",
            Name = "Driver",
            EboekhoudenCode = "WIM"
        });
        _db.Users.Add(new User
        {
            Id = 8,
            Email = "other@example.com",
            Name = "Other",
            EboekhoudenCode = "OLD"
        });
        _db.Users.Add(new User
        {
            Id = 9,
            Email = "nocode@example.com",
            Name = "No Code"
        });
        _db.ChargeCards.Add(new ChargeCard { Id = 3, Value = "CARD-WIM" });
        _db.ChargeCards.Add(new ChargeCard { Id = 4, Value = "CARD-OLD" });
        _db.ChargeCards.Add(new ChargeCard { Id = 5, Value = "CARD-NOCODE" });
        _db.UserChargeCards.Add(new UserChargeCard { UserId = 7, ChargeCardId = 3 });
        _db.UserChargeCards.Add(new UserChargeCard { UserId = 8, ChargeCardId = 4 });
        _db.UserChargeCards.Add(new UserChargeCard { UserId = 9, ChargeCardId = 5 });
        _db.ChargingSessions.Add(new ChargingSession
        {
            SerialNumber = "5821063866",
            Rfid = "CARD-WIM",
            ChargeCardId = 3,
            StationId = 1,
            FromTimestamp = 1_700_000_000_000,
            ToTimestamp = null
        });
        _db.ChargingSessions.Add(new ChargingSession
        {
            SerialNumber = "5821063800",
            Rfid = "CARD-OLD",
            ChargeCardId = 4,
            StationId = 2,
            FromTimestamp = 1_600_000_000_000,
            ToTimestamp = 1_600_000_100_000
        });
        _db.ChargingSessions.Add(new ChargingSession
        {
            SerialNumber = "5821063911",
            Rfid = "CARD-NOCODE",
            ChargeCardId = 5,
            StationId = 3,
            FromTimestamp = 1_700_000_000_000,
            ToTimestamp = null
        });
        _db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now.AddMinutes(-5),
            IsSuccess = true,
            PayloadJson = """
                          {
                            "FetchedAt":"2026-11-17T17:55:00Z",
                            "ServiceLocationId":1,
                            "Points":[
                              {"SerialNumber":"5821063866","StationName":"Paal 1","UiState":"charging","Rfid":"CARD-WIM","EnergyKwh":17.6,"StartedAtUtc":"2026-11-17T17:00:00Z"}
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        _db.SmappeeStatusSnapshots.Add(new SmappeeStatusSnapshot
        {
            ServiceLocationId = 1,
            FetchedAtUtc = now,
            IsSuccess = true,
            PayloadJson = """
                          {
                            "FetchedAt":"2026-11-17T18:00:00Z",
                            "ServiceLocationId":1,
                            "Points":[
                              {"SerialNumber":"5821063866","StationName":"Paal 1","UiState":"charging","Rfid":"CARD-WIM","EnergyKwh":18.4,"StartedAtUtc":"2026-11-17T17:00:00Z"},
                              {"SerialNumber":"5821063800","StationName":"Paal 2","UiState":"available","Rfid":"CARD-OLD"},
                              {"SerialNumber":"5821063911","StationName":"Paal 3","UiState":"charging","Rfid":"CARD-NOCODE"},
                              {"SerialNumber":"5821063919","StationName":"Paal 4","UiState":"suspended","Rfid":"CARD-NOCODE"}
                            ],
                            "RangeStartMs":0,
                            "RangeEndMsExclusive":0
                          }
                          """
        });
        await _db.SaveChangesAsync();

        var result = await BuildController().GetPublicDisplay(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("CARD-WIM", json);
        Assert.DoesNotContain("CARD-OLD", json);
        Assert.DoesNotContain("CARD-NOCODE", json);
        using var doc = JsonDocument.Parse(json);
        var stations = doc.RootElement.GetProperty("stations").EnumerateArray().ToList();
        var busy = stations.Single(s => s.GetProperty("name").GetString() == "Paal 1");
        var free = stations.Single(s => s.GetProperty("name").GetString() == "Paal 2");
        var noCode = stations.Single(s => s.GetProperty("name").GetString() == "Paal 3");
        Assert.True(busy.GetProperty("inUse").GetBoolean());
        Assert.Equal("WIM", busy.GetProperty("userCode").GetString());
        Assert.Equal(18.4m, busy.GetProperty("energyKwh").GetDecimal());
        Assert.Equal(9600, busy.GetProperty("powerWatts").GetInt32());
        Assert.Equal(JsonValueKind.Null, busy.GetProperty("endedAtUtc").ValueKind);
        Assert.False(free.GetProperty("inUse").GetBoolean());
        Assert.Equal(JsonValueKind.Null, free.GetProperty("userCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, free.GetProperty("energyKwh").ValueKind);
        Assert.Equal(JsonValueKind.Null, free.GetProperty("powerWatts").ValueKind);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_600_000_100_000).UtcDateTime,
            free.GetProperty("endedAtUtc").GetDateTime().ToUniversalTime());
        Assert.True(noCode.GetProperty("inUse").GetBoolean());
        Assert.Equal("9", noCode.GetProperty("userCode").GetString());
        Assert.Equal(JsonValueKind.Null, noCode.GetProperty("powerWatts").ValueKind);
        var paused = stations.Single(s => s.GetProperty("name").GetString() == "Paal 4");
        Assert.True(paused.GetProperty("inUse").GetBoolean());
        Assert.Equal(0, paused.GetProperty("powerWatts").GetInt32());
    }

}
