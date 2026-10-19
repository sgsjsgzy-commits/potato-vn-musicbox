using System;
using AutoMapper;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Models;
using GalgameManager.Server.Repositories;
using GalgameManager.Server.Services;
using Moq;

namespace GalgameManager.Server.Test.Services;

/// <summary>
/// GalgameService redirect 功能测试
/// </summary>
[TestFixture]
public class GalgameServiceRedirectTests : TestBase
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

    #region GetGalgameAsync Redirect Tests

    [Test]
    public async Task GetGalgameAsync_WithRedirect_ReturnsTargetGame()
    {
        // Arrange
        var targetGame = new Galgame { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var sourceGame = new Galgame { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        // Act
        var result = await _service.GetGalgameAsync(TestUserId, sourceGame.Id);

        // Assert
        Assert.That(result.Id, Is.EqualTo(targetGame.Id));
        Assert.That(result.Name, Is.EqualTo("Target Game"));
    }

    [Test]
    public void GetGalgameAsync_WrongUser_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var game = new Galgame { UserId = TestUserId, Name = "Game", RedirectTo = 0 };
        Context.Galgame.Add(game);
        Context.SaveChanges();

        // Act & Assert
        Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await _service.GetGalgameAsync(TestUserId + 1, game.Id));
    }

    #endregion

    #region AddOrUpdateGalgameAsync Redirect Tests

    [Test]
    public async Task AddOrUpdateGalgameAsync_UpdateRedirectedGame_UpdatesTargetGame()
    {
        // Arrange
        var targetGame = new Galgame { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var sourceGame = new Galgame { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        var updateDto = new GalgameUpdateDto
        {
            Id = sourceGame.Id,  // 使用 source 的 ID
            Name = "Updated Name"
        };

        // Act
        var result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 应该更新目标游戏
        Assert.That(result.Id, Is.EqualTo(targetGame.Id));
        Assert.That(result.Name, Is.EqualTo("Updated Name"));

        // 验证数据库中目标游戏已更新
        var dbTargetGame = await Context.Galgame.FindAsync(targetGame.Id);
        Assert.That(dbTargetGame!.Name, Is.EqualTo("Updated Name"));
    }

    #endregion

    #region AddPlayLogAsync Redirect Tests

    [Test]
    public async Task AddPlayLogAsync_WithRedirect_AddsToTargetGame()
    {
        // Arrange
        var targetGame = new Galgame { UserId = TestUserId, Name = "Target Game", RedirectTo = 0, TotalPlayTime = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var sourceGame = new Galgame { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        var playLogDto = new PlayLogDto { DateTimeStamp = 123456, Minute = 60 };

        // Act - 使用 source 的 ID 添加 play log
        var result = await _service.AddPlayLogAsync(TestUserId, sourceGame.Id, playLogDto);

        // Assert - 应该更新目标游戏的 TotalPlayTime
        Assert.That(result!.Id, Is.EqualTo(targetGame.Id));
        Assert.That(result.TotalPlayTime, Is.EqualTo(60));

        // 验证 PlayLog 是为目标游戏创建的
        _playLogRepMock.Verify(x => x.AddOrUpdatePlayLogAsync(
            It.Is<PlayLog>(p => p.GalgameId == targetGame.Id && p.Minute == 60)), Times.Once);
    }

    #endregion

    #region DeleteGalgameAsync Redirect Tests

    [Test]
    public async Task DeleteGalgameAsync_WithRedirectChain_DeletesAllGamesInChain()
    {
        // Arrange: A -> B -> C (target)
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act - 删除 gameA（会 redirect 到 gameC）
        await _service.DeleteGalgameAsync(TestUserId, gameA.Id);

        // Assert - 所有游戏都应该被删除
        Assert.That(await Context.Galgame.FindAsync(gameA.Id), Is.Null);
        Assert.That(await Context.Galgame.FindAsync(gameB.Id), Is.Null);
        Assert.That(await Context.Galgame.FindAsync(gameC.Id), Is.Null);

        // 验证创建了删除记录
        _galDeletedRepMock.Verify(x => x.AddGalgameDeletedAsync(
            It.Is<GalgameDeleted>(g => g.GalgameId == gameA.Id)), Times.Once);
        _galDeletedRepMock.Verify(x => x.AddGalgameDeletedAsync(
            It.Is<GalgameDeleted>(g => g.GalgameId == gameB.Id)), Times.Once);
        _galDeletedRepMock.Verify(x => x.AddGalgameDeletedAsync(
            It.Is<GalgameDeleted>(g => g.GalgameId == gameC.Id)), Times.Once);
    }

    [Test]
    public async Task DeleteGalgameAsync_MultipleSourcesPointToSameTarget_DeletesAllSources()
    {
        // Arrange: A -> C, B -> C
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameC.Id };
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.AddRange(gameA, gameB);
        await Context.SaveChangesAsync();

        // Act - 删除 gameC
        await _service.DeleteGalgameAsync(TestUserId, gameC.Id);

        // Assert - 所有游戏都应该被删除
        Assert.That(await Context.Galgame.FindAsync(gameA.Id), Is.Null);
        Assert.That(await Context.Galgame.FindAsync(gameB.Id), Is.Null);
        Assert.That(await Context.Galgame.FindAsync(gameC.Id), Is.Null);
    }

    [Test]
    public async Task DeleteGalgameAsync_WithOssResources_DeletesAllOssResources()
    {
        // Arrange
        var targetGame = new Galgame
        {
            UserId = TestUserId,
            Name = "Target Game",
            RedirectTo = 0,
            ImageLoc = "target/image.jpg",
            HeaderImageOssPosition = "target/header.jpg"
        };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var sourceGame = new Galgame
        {
            UserId = TestUserId,
            Name = "Source Game",
            RedirectTo = targetGame.Id,
            ImageLoc = "source/image.jpg",
            HeaderImageOssPosition = "source/header.jpg"
        };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        // Act
        await _service.DeleteGalgameAsync(TestUserId, sourceGame.Id);

        // Assert - 验证所有 OSS 资源都被删除
        _ossServiceMock.Verify(x => x.DeleteObjectAsync(TestUserId, "target/image.jpg"), Times.Once);
        _ossServiceMock.Verify(x => x.DeleteObjectAsync(TestUserId, "target/header.jpg"), Times.Once);
        _ossServiceMock.Verify(x => x.DeleteObjectAsync(TestUserId, "source/image.jpg"), Times.Once);
        _ossServiceMock.Verify(x => x.DeleteObjectAsync(TestUserId, "source/header.jpg"), Times.Once);
    }

    [Test]
    public async Task DeleteGalgameAsync_NoRedirect_OnlyDeletesSingleGame()
    {
        // Arrange
        var game = new Galgame { UserId = TestUserId, Name = "Single Game", RedirectTo = 0 };
        Context.Galgame.Add(game);
        await Context.SaveChangesAsync();

        // Act
        await _service.DeleteGalgameAsync(TestUserId, game.Id);

        // Assert
        Assert.That(await Context.Galgame.FindAsync(game.Id), Is.Null);
        _galDeletedRepMock.Verify(x => x.AddGalgameDeletedAsync(
            It.IsAny<GalgameDeleted>()), Times.Once);
    }

    #endregion
}
