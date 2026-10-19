using GalgameManager.Server.Contracts;
using GalgameManager.Server.Models;
using GalgameManager.Server.Repositories;

namespace GalgameManager.Server.Test.Repositories;

/// <summary>
/// GalgameRepository redirect 功能测试
/// </summary>
[TestFixture]
public class GalgameRepositoryRedirectTests : TestBase
{
    private IGalgameRepository _repository = null!;
    private const int TestUserId = 1;

    public override void Setup()
    {
        base.Setup();
        _repository = new GalgameRepository(Context);
    }

    #region GetGalgameAsync Redirect Tests

    [Test]
    public async Task GetGalgameAsync_NoRedirect_ReturnsOriginalGame()
    {
        // Arrange
        var game = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = 0 };
        Context.Galgame.Add(game);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgameAsync(game.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(game.Id));
        Assert.That(result.Name, Is.EqualTo("Game A"));
    }

    [Test]
    public async Task GetGalgameAsync_SingleRedirect_ReturnsTargetGame()
    {
        // Arrange
        var targetGame = new Galgame { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var sourceGame = new Galgame { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgameAsync(sourceGame.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(targetGame.Id));
        Assert.That(result.Name, Is.EqualTo("Target Game"));
    }

    [Test]
    public async Task GetGalgameAsync_ChainRedirect_ReturnsLastGame()
    {
        // Arrange: A -> B -> C
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgameAsync(gameA.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(gameC.Id));
        Assert.That(result.Name, Is.EqualTo("Game C"));
    }

    [Test]
    public async Task GetGalgameAsync_CircularRedirect_BreaksLoop()
    {
        // Arrange: A -> B -> A (circular)
        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = 0 };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameA.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        // 更新 A 指向 B，形成循环
        gameA.RedirectTo = gameB.Id;
        await Context.SaveChangesAsync();

        // Act - 不应该抛出异常或无限循环
        var result = await _repository.GetGalgameAsync(gameA.Id);

        // Assert - 应该返回循环中的某个游戏
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GetGalgameAsync_RedirectToNonExistent_ReturnsNull()
    {
        // Arrange
        var game = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = 99999 };
        Context.Galgame.Add(game);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgameAsync(game.Id);

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region GetGalgameCompleteAsync Redirect Tests

    [Test]
    public async Task GetGalgameCompleteAsync_SingleRedirect_ReturnsTargetGameWithRelations()
    {
        // Arrange
        var targetGame = new Galgame { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var playLog = new PlayLog { GalgameId = targetGame.Id, DateTimeStamp = 123456, Minute = 60 };
        Context.GalPlayLog.Add(playLog);
        await Context.SaveChangesAsync();

        var sourceGame = new Galgame { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgameCompleteAsync(sourceGame.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(targetGame.Id));
        Assert.That(result.PlayTime, Is.Not.Null);
        Assert.That(result.PlayTime, Has.Count.EqualTo(1));
    }

    #endregion

    #region GetGalgamesAsync excludeRedirected Tests

    [Test]
    public async Task GetGalgamesAsync_ExcludeRedirected_OnlyReturnsNonRedirectedGames()
    {
        // Arrange
        var targetGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Target Game", 
            RedirectTo = 0,
            LastChangedTimeStamp = 100
        };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var redirectedGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Redirected Game", 
            RedirectTo = targetGame.Id,
            LastChangedTimeStamp = 100
        };
        Context.Galgame.Add(redirectedGame);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgamesAsync(TestUserId, 0, 0, 10, excludeRedirected: true);

        // Assert
        Assert.That(result.Items, Has.Count.EqualTo(1));
        Assert.That(result.Items[0].Id, Is.EqualTo(targetGame.Id));
        Assert.That(result.Cnt, Is.EqualTo(1));
    }

    [Test]
    public async Task GetGalgamesAsync_IncludeRedirected_ReturnsAllGames()
    {
        // Arrange
        var targetGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Target Game", 
            RedirectTo = 0,
            LastChangedTimeStamp = 100
        };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var redirectedGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Redirected Game", 
            RedirectTo = targetGame.Id,
            LastChangedTimeStamp = 100
        };
        Context.Galgame.Add(redirectedGame);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgamesAsync(TestUserId, 0, 0, 10, excludeRedirected: false);

        // Assert
        Assert.That(result.Items, Has.Count.EqualTo(2));
        Assert.That(result.Cnt, Is.EqualTo(2));
    }

    [Test]
    public async Task GetGalgamesAsync_DefaultParameter_ExcludesRedirected()
    {
        // Arrange
        var targetGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Target Game", 
            RedirectTo = 0,
            LastChangedTimeStamp = 100
        };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        var redirectedGame = new Galgame 
        { 
            UserId = TestUserId, 
            Name = "Redirected Game", 
            RedirectTo = targetGame.Id,
            LastChangedTimeStamp = 100
        };
        Context.Galgame.Add(redirectedGame);
        await Context.SaveChangesAsync();

        // Act - 使用默认参数
        var result = await _repository.GetGalgamesAsync(TestUserId, 0, 0, 10);

        // Assert
        Assert.That(result.Items, Has.Count.EqualTo(1));
        Assert.That(result.Items[0].Id, Is.EqualTo(targetGame.Id));
    }

    #endregion

    #region GetRedirectChainAsync Tests

    [Test]
    public async Task GetRedirectChainAsync_NoRedirects_ReturnsEmptyList()
    {
        // Arrange
        var game = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = 0 };
        Context.Galgame.Add(game);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetRedirectChainAsync(game.Id);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetRedirectChainAsync_SingleRedirect_ReturnsSourceGame()
    {
        // Arrange: A -> B
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = 0 };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetRedirectChainAsync(gameB.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Contains.Item(gameA.Id));
    }

    [Test]
    public async Task GetRedirectChainAsync_ChainRedirect_ReturnsAllSourceGames()
    {
        // Arrange: A -> B -> C
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetRedirectChainAsync(gameC.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Contains.Item(gameA.Id));
        Assert.That(result, Contains.Item(gameB.Id));
    }

    [Test]
    public async Task GetRedirectChainAsync_MultipleSourcesPointToSameTarget_ReturnsAllSources()
    {
        // Arrange: A -> C, B -> C
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameC.Id };
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.AddRange(gameA, gameB);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetRedirectChainAsync(gameC.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Contains.Item(gameA.Id));
        Assert.That(result, Contains.Item(gameB.Id));
    }

    [Test]
    public async Task GetRedirectChainAsync_ComplexChain_ReturnsAllSources()
    {
        // Arrange: A -> C, B -> C, D -> B -> C (complex chain)
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameC.Id };
        Context.Galgame.AddRange(gameA, gameB);
        await Context.SaveChangesAsync();

        var gameD = new Galgame { UserId = TestUserId, Name = "Game D", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameD);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetRedirectChainAsync(gameC.Id);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Contains.Item(gameA.Id));
        Assert.That(result, Contains.Item(gameB.Id));
        Assert.That(result, Contains.Item(gameD.Id));
    }

    #endregion

    #region GetGalgamesAsync(List<int> ids) Redirect Tests

    [Test]
    public async Task GetGalgamesAsync_ByIds_NoRedirect_ReturnsAllGames()
    {
        // Arrange
        var game1 = new Galgame { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        var game2 = new Galgame { UserId = TestUserId, Name = "Game 2", RedirectTo = 0 };
        Context.Galgame.AddRange(game1, game2);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgamesAsync([game1.Id, game2.Id]);

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(g => g.Id), Contains.Item(game1.Id));
        Assert.That(result.Select(g => g.Id), Contains.Item(game2.Id));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_SingleRedirect_ReturnsTargetGame()
    {
        // Arrange: A -> B
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = 0 };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act - 请求 gameA，应该返回 gameB（重定向目标）
        var result = await _repository.GetGalgamesAsync([gameA.Id]);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(gameB.Id));
        Assert.That(result[0].Name, Is.EqualTo("Game B"));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_ChainRedirect_ReturnsLastGame()
    {
        // Arrange: A -> B -> C
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act
        var result = await _repository.GetGalgamesAsync([gameA.Id]);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(gameC.Id));
        Assert.That(result[0].Name, Is.EqualTo("Game C"));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_MultipleIdsRedirectToSameTarget_ReturnsOneTarget()
    {
        // Arrange: A -> C, B -> C
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameC.Id };
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.AddRange(gameA, gameB);
        await Context.SaveChangesAsync();

        // Act - 请求 gameA 和 gameB，它们都重定向到 gameC，应该只返回一个 gameC
        var result = await _repository.GetGalgamesAsync([gameA.Id, gameB.Id]);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(gameC.Id));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_MixedRedirectAndNonRedirect_ReturnsCorrectGames()
    {
        // Arrange: A -> C (redirect), B (no redirect)
        var gameC = new Galgame { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = 0 };
        Context.Galgame.AddRange(gameC, gameB);
        await Context.SaveChangesAsync();

        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = gameC.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        // Act - 请求 gameA 和 gameB
        var result = await _repository.GetGalgamesAsync([gameA.Id, gameB.Id]);

        // Assert - 应该返回 gameC（从 A 重定向）和 gameB
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(g => g.Id), Contains.Item(gameC.Id));
        Assert.That(result.Select(g => g.Id), Contains.Item(gameB.Id));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_RedirectToNonExistent_ExcludesInvalidGame()
    {
        // Arrange
        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = 99999 };
        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = 0 };
        Context.Galgame.AddRange(gameA, gameB);
        await Context.SaveChangesAsync();

        // Act - 请求 gameA（重定向到不存在的游戏）和 gameB
        var result = await _repository.GetGalgamesAsync([gameA.Id, gameB.Id]);

        // Assert - gameA 应该被排除（因为重定向目标不存在），只返回 gameB
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Id, Is.EqualTo(gameB.Id));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_CircularRedirect_ReturnsOneGame()
    {
        // Arrange: A -> B -> A (circular)
        var gameA = new Galgame { UserId = TestUserId, Name = "Game A", RedirectTo = 0 };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        var gameB = new Galgame { UserId = TestUserId, Name = "Game B", RedirectTo = gameA.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        gameA.RedirectTo = gameB.Id;
        await Context.SaveChangesAsync();

        // Act - 不应该无限循环
        var result = await _repository.GetGalgamesAsync([gameA.Id]);

        // Assert - 应该返回循环中的某个游戏
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_EmptyList_ReturnsEmptyList()
    {
        // Act
        var result = await _repository.GetGalgamesAsync([]);

        // Assert
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetGalgamesAsync_ByIds_NonExistentIds_ReturnsEmptyList()
    {
        // Act
        var result = await _repository.GetGalgamesAsync([99999, 99998]);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion
}
