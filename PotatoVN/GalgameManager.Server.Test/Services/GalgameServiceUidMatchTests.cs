using AutoMapper;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Models;
using GalgameManager.Server.Repositories;
using GalgameManager.Server.Services;
using Moq;

namespace GalgameManager.Server.Test.Services;

/// <summary>
/// GalgameService UID 匹配功能测试
/// 当 AddOrUpdateGalgameAsync 的 Id 为 null 时，应通过 BgmId、VndbId 或 Name 查找现有游戏
/// </summary>
[TestFixture]
public class GalgameServiceUidMatchTests : TestBase
{
    private IGalgameService _service = null!;
    private IGalgameRepository _galRepository = null!;
    private Mock<IGalgameDeletedRepository> _galDeletedRepMock = null!;
    private Mock<IPlayLogRepository> _playLogRepMock = null!;
    private Mock<ICharacterRepository> _characterRepMock = null!;
    private Mock<IUserService> _userServiceMock = null!;
    private Mock<IOssService> _ossServiceMock = null!;
    private Mock<IMapper> _mapperMock = null!;
    
    private const int TestUserId = 1;
    private const int OtherUserId = 2;

    public override void Setup()
    {
        base.Setup();
        
        _galRepository = new GalgameRepository(Context);
        _galDeletedRepMock = new Mock<IGalgameDeletedRepository>();
        _playLogRepMock = new Mock<IPlayLogRepository>();
        _characterRepMock = new Mock<ICharacterRepository>();
        _userServiceMock = new Mock<IUserService>();
        _ossServiceMock = new Mock<IOssService>();
        _mapperMock = new Mock<IMapper>();

        // 设置默认Mock行为
        _galDeletedRepMock
            .Setup(x => x.AddGalgameDeletedAsync(It.IsAny<GalgameDeleted>()))
            .ReturnsAsync((GalgameDeleted g) => g);
        _userServiceMock
            .Setup(x => x.UpdateLastModifiedAsync(It.IsAny<int>(), It.IsAny<long>()))
            .Returns(Task.CompletedTask);
        _ossServiceMock
            .Setup(x => x.DeleteObjectAsync(It.IsAny<int>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _playLogRepMock
            .Setup(x => x.GetPlayLogAsync(It.IsAny<int>(), It.IsAny<long>()))
            .ReturnsAsync((PlayLog?)null);
        _playLogRepMock
            .Setup(x => x.AddOrUpdatePlayLogAsync(It.IsAny<PlayLog>()))
            .ReturnsAsync((PlayLog p) => p);
        _playLogRepMock
            .Setup(x => x.SetPlayLogsAsync(It.IsAny<int>(), It.IsAny<List<PlayLog>>()))
            .Returns(Task.CompletedTask);

        _service = new GalgameService(
            _galRepository,
            _galDeletedRepMock.Object,
            _playLogRepMock.Object,
            _characterRepMock.Object,
            _userServiceMock.Object,
            _ossServiceMock.Object,
            _mapperMock.Object
        );
    }

    #region AddOrUpdateGalgameAsync with null Id - UID Match Tests

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_NoExistingGame_CreatesNewGame()
    {
        // Arrange
        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "12345",
            VndbId = "v1234",
            Name = "New Game"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert
        Assert.That(result.Id, Is.GreaterThan(0));
        Assert.That(result.UserId, Is.EqualTo(TestUserId));
        Assert.That(result.BgmId, Is.EqualTo("12345"));
        Assert.That(result.VndbId, Is.EqualTo("v1234"));
        Assert.That(result.Name, Is.EqualTo("New Game"));
        
        // 验证数据库中只有一个游戏
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_MatchByBgmId_UpdatesExistingGame()
    {
        // Arrange - 创建一个已有的游戏
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "12345",
            Name = "Existing Game",
            Description = "Original Description"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();
        var existingGameId = existingGame.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null, // Id 为空
            BgmId = "12345", // 与现有游戏的 BgmId 相同
            Name = "Updated Game Name",
            Description = "Updated Description"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应该更新现有游戏而不是创建新游戏
        Assert.That(result.Id, Is.EqualTo(existingGameId));
        Assert.That(result.BgmId, Is.EqualTo("12345"));
        Assert.That(result.Name, Is.EqualTo("Updated Game Name"));
        Assert.That(result.Description, Is.EqualTo("Updated Description"));
        
        // 验证数据库中仍然只有一个游戏
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_MatchByVndbId_UpdatesExistingGame()
    {
        // Arrange - 创建一个已有的游戏（只有 VndbId）
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            VndbId = "v5678",
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();
        var existingGameId = existingGame.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            VndbId = "v5678", // 与现有游戏的 VndbId 相同
            Name = "New Name"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert
        Assert.That(result.Id, Is.EqualTo(existingGameId));
        Assert.That(result.Name, Is.EqualTo("New Name"));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_HasId_DoesNotUseNameMatch_CreatesNewGame()
    {
        // Arrange - 创建一个已有的游戏（只有 Name，但上传带有 BgmId）
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Same Name Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "12345", // 有 BgmId
            VndbId = null,
            Name = "Same Name Game", // 与现有游戏的 Name 相同
            Developer = "New Developer"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 因为有 BgmId，不应使用 Name 匹配，应创建新游戏
        Assert.That(result.Id, Is.Not.EqualTo(existingGame.Id));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_NoId_MatchByName_UpdatesExistingGame()
    {
        // Arrange - 创建一个已有的游戏（只有 Name）
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Same Name Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();
        var existingGameId = existingGame.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = null, // 没有 BgmId
            VndbId = null, // 没有 VndbId
            Name = "Same Name Game", // 与现有游戏的 Name 相同
            Developer = "New Developer"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 因为没有任何 ID，应使用 Name 匹配
        Assert.That(result.Id, Is.EqualTo(existingGameId));
        Assert.That(result.Developer, Is.EqualTo("New Developer"));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_BgmIdMatch_NoVndbIdConflict_UpdatesGame()
    {
        // Arrange - 创建一个有 BgmId 但没有 VndbId 的游戏
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "11111",
            VndbId = null, // 没有 VndbId
            Name = "Game With BgmId"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();
        var existingGameId = existingGame.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "11111", // BgmId 匹配
            VndbId = "v22222", // 上传带有 VndbId，但已有游戏没有，不冲突
            Name = "Updated Name"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应匹配并更新游戏（因为已有游戏的 VndbId 为 null，不冲突）
        Assert.That(result.Id, Is.EqualTo(existingGameId));
        Assert.That(result.Name, Is.EqualTo("Updated Name"));
        Assert.That(result.VndbId, Is.EqualTo("v22222"));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_VndbIdMatch_HasId_DoesNotUseNameMatch()
    {
        // Arrange - 创建两个游戏：一个有目标 VndbId，另一个有目标 Name
        var gameVndb = new Galgame 
        { 
            UserId = TestUserId, 
            VndbId = "v33333",
            Name = "Game A"
        };
        var gameName = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Target Name"
        };
        Context.Galgame.AddRange(gameVndb, gameName);
        await Context.SaveChangesAsync();
        var gameVndbId = gameVndb.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = null, // 没有 BgmId
            VndbId = "v33333", // VndbId 匹配
            Name = "Target Name" // Name 也存在，但因为有 VndbId，不应使用 Name 匹配
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应匹配 VndbId 对应的游戏，而不是 Name 对应的游戏
        Assert.That(result.Id, Is.EqualTo(gameVndbId));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_IdConflict_CreatesNewGame()
    {
        // Arrange - 创建一个有 BgmId 和 VndbId 的游戏
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "11111",
            VndbId = "v11111",
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "11111", // BgmId 匹配
            VndbId = "v99999", // VndbId 不匹配，产生冲突
            Name = "New Game"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 由于 VndbId 冲突，应创建新游戏
        Assert.That(result.Id, Is.Not.EqualTo(existingGame.Id));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_VndbIdMatch_BgmIdConflict_CreatesNewGame()
    {
        // Arrange - 创建一个有 BgmId 和 VndbId 的游戏
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "22222",
            VndbId = "v22222",
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "99999", // BgmId 不匹配，产生冲突
            VndbId = "v22222", // VndbId 匹配
            Name = "New Game"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 由于 BgmId 冲突，应创建新游戏
        Assert.That(result.Id, Is.Not.EqualTo(existingGame.Id));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_OneNullId_NoConflict_UpdatesGame()
    {
        // Arrange - 创建一个只有 BgmId 的游戏
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "33333",
            VndbId = null, // 没有 VndbId
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();
        var existingGameId = existingGame.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "33333", // BgmId 匹配
            VndbId = "v33333", // 上传有 VndbId，但已有游戏没有，不算冲突
            Name = "Updated Name"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 没有冲突，应更新
        Assert.That(result.Id, Is.EqualTo(existingGameId));
        Assert.That(result.VndbId, Is.EqualTo("v33333"));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_ExistingHasVndbId_UploadNull_NoConflict_UpdatesGame()
    {
        // Arrange - 创建一个有 VndbId 的游戏
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "44444",
            VndbId = "v44444",
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();
        var existingGameId = existingGame.Id;

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "44444", // BgmId 匹配
            VndbId = null, // 上传没有 VndbId，不算冲突
            Name = "Updated Name"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 没有冲突，应更新
        Assert.That(result.Id, Is.EqualTo(existingGameId));
        Assert.That(result.VndbId, Is.EqualTo("v44444")); // 保留原有 VndbId
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_DifferentUser_DoesNotMatch_CreatesNewGame()
    {
        // Arrange - 其他用户有一个相同 BgmId 的游戏
        var otherUserGame = new Galgame 
        { 
            UserId = OtherUserId, 
            BgmId = "99999",
            Name = "Other User Game"
        };
        Context.Galgame.Add(otherUserGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "99999", // 与其他用户的游戏相同
            Name = "My Game"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应创建新游戏，不应匹配其他用户的游戏
        Assert.That(result.Id, Is.Not.EqualTo(otherUserGame.Id));
        Assert.That(result.UserId, Is.EqualTo(TestUserId));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(2)); // 两个用户各有一个游戏
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_RedirectedGame_NotMatched_CreatesNewGame()
    {
        // Arrange - 创建一个已被 redirect 的游戏
        var targetGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "77777",
            Name = "Target Game",
            RedirectTo = 0
        };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var redirectedGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "88888",
            Name = "Redirected Game",
            RedirectTo = targetGame.Id // 被 redirect 到 targetGame
        };
        Context.Galgame.Add(redirectedGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "88888", // 匹配被 redirect 的游戏的 BgmId
            Name = "New Game"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 不应匹配被 redirect 的游戏，应创建新游戏
        Assert.That(result.Id, Is.Not.EqualTo(redirectedGame.Id));
        Assert.That(result.Id, Is.Not.EqualTo(targetGame.Id));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(3)); // 三个游戏
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_PartialMatch_BgmIdMatches_UpdatesGame()
    {
        // Arrange - 游戏只有 BgmId，没有 VndbId
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "44444",
            VndbId = null, // 没有 VndbId
            Name = "Partial Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "44444",
            VndbId = "v55555", // 提供了新的 VndbId
            Name = "Updated Name"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应通过 BgmId 匹配并更新
        Assert.That(result.Id, Is.EqualTo(existingGame.Id));
        Assert.That(result.VndbId, Is.EqualTo("v55555")); // VndbId 应该被更新
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_WithId_StillUsesExistingLogic()
    {
        // Arrange - 使用现有 Id 更新
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "66666",
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = existingGame.Id, // 提供了 Id
            Name = "Updated Via Id"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert
        Assert.That(result.Id, Is.EqualTo(existingGame.Id));
        Assert.That(result.Name, Is.EqualTo("Updated Via Id"));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_EmptyUids_CreatesNewGame()
    {
        // Arrange - 所有 UID 字段都为空或 null
        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = null,
            VndbId = null,
            Name = null,
            Developer = "Some Developer"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应创建新游戏
        Assert.That(result.Id, Is.GreaterThan(0));
        Assert.That(result.UserId, Is.EqualTo(TestUserId));
        Assert.That(result.Developer, Is.EqualTo("Some Developer"));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullId_EmptyStringUids_CreatesNewGame()
    {
        // Arrange - UID 字段为空字符串
        var existingGame = new Galgame 
        { 
            UserId = TestUserId, 
            BgmId = "12345",
            Name = "Existing Game"
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = null,
            BgmId = "", // 空字符串，不应匹配
            VndbId = "",
            Name = "",
            Developer = "New Developer"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应创建新游戏（空字符串不应用于匹配）
        Assert.That(result.Id, Is.Not.EqualTo(existingGame.Id));
        Assert.That(Context.Galgame.Count(), Is.EqualTo(2));
    }

    #endregion
}
