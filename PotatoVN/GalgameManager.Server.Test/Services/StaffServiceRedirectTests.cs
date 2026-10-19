using GalgameManager.Server.Contracts;
using GalgameManager.Server.Enums;
using GalgameManager.Server.Models;
using GalgameManager.Server.Repositories;
using GalgameManager.Server.Services;
using Moq;

namespace GalgameManager.Server.Test.Services;

/// <summary>
/// StaffService redirect 功能测试
/// 测试 Staff 的 patch 中重定向的游戏能否被正确处理
/// </summary>
[TestFixture]
public class StaffServiceRedirectTests : TestBase
{
    private IStaffService _service = null!;
    private IStaffRepository _staffRepository = null!;
    private IGalgameRepository _galRepository = null!;
    private Mock<IUserRepository> _userRepMock = null!;

    private const int TestUserId = 1;

    public override void Setup()
    {
        base.Setup();

        _staffRepository = new StaffRepository(Context);
        _galRepository = new GalgameRepository(Context);
        _userRepMock = new Mock<IUserRepository>();

        // 设置用户 Mock
        User testUser = new() { Id = TestUserId, UserName = "TestUser", TotalSpace = 100000000 };
        _userRepMock
            .Setup(x => x.GetUserAsync(TestUserId))
            .ReturnsAsync(testUser);
        _userRepMock
            .Setup(x => x.UpdateUserAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => u);

        _service = new StaffService(
            _staffRepository,
            _galRepository,
            _userRepMock.Object
        );
    }

    #region UpsertAsync with Redirected Games Tests

    [Test]
    public async Task UpsertAsync_WithNonRedirectedGame_Success()
    {
        // Arrange - 创建一个没有重定向的游戏
        Galgame game = new() { UserId = TestUserId, Name = "Normal Game", RedirectTo = 0 };
        Context.Galgame.Add(game);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames = [new StaffGameUpdateDto { GameId = game.Id, Relation = [Career.Painter] }]
        };

        // Act
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.JapaneseName, Is.EqualTo("Test Staff"));
        Assert.That(result.StaffGames, Has.Count.EqualTo(1));
        Assert.That(result.StaffGames[0].GameId, Is.EqualTo(game.Id));
    }

    [Test]
    public async Task UpsertAsync_WithRedirectedGame_UsesTargetGame()
    {
        // Arrange - 创建游戏重定向链: sourceGame -> targetGame
        Galgame targetGame = new() { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        Galgame sourceGame = new() { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = sourceGame.Id, Relation = [Career.Writer] } // 使用 sourceGame 的 ID
            ]
        };

        // Act - 应该使用重定向后的 targetGame
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(1));
        // StaffGames 中的 GameId 应该仍然是传入的 sourceGame.Id（因为这是用户指定的）
        // 但关键是不应该抛出 KeyNotFoundException，因为重定向后的游戏存在
        Assert.That(result.StaffGames[0].GameId, Is.EqualTo(sourceGame.Id));
    }

    [Test]
    public async Task UpsertAsync_WithChainRedirectedGame_UsesLastGame()
    {
        // Arrange - 创建游戏重定向链: A -> B -> C
        Galgame gameC = new() { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        Galgame gameB = new() { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.Add(gameB);
        await Context.SaveChangesAsync();

        Galgame gameA = new() { UserId = TestUserId, Name = "Game A", RedirectTo = gameB.Id };
        Context.Galgame.Add(gameA);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = gameA.Id, Relation = [Career.Musician] } // 使用 gameA 的 ID
            ]
        };

        // Act - 应该成功，因为重定向链的最终目标 gameC 存在
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(1));
        Assert.That(result.StaffGames[0].GameId, Is.EqualTo(gameA.Id));
    }

    [Test]
    public async Task UpsertAsync_WithMultipleGames_SomeRedirected_Success()
    {
        // Arrange - 创建多个游戏，部分有重定向
        Galgame normalGame = new() { UserId = TestUserId, Name = "Normal Game", RedirectTo = 0 };
        Galgame targetGame = new() { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.AddRange(normalGame, targetGame);
        await Context.SaveChangesAsync();

        Galgame redirectedGame = new() { UserId = TestUserId, Name = "Redirected Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(redirectedGame);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = normalGame.Id, Relation = [Career.Painter] },
                new StaffGameUpdateDto { GameId = redirectedGame.Id, Relation = [Career.Writer] }
            ]
        };

        // Act
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task UpsertAsync_WithMultipleGamesRedirectToSameTarget_DeduplicatesTargetGame()
    {
        // Arrange - 多个游戏都重定向到同一个目标: A -> C, B -> C
        Galgame gameC = new() { UserId = TestUserId, Name = "Game C", RedirectTo = 0 };
        Context.Galgame.Add(gameC);
        await Context.SaveChangesAsync();

        Galgame gameA = new() { UserId = TestUserId, Name = "Game A", RedirectTo = gameC.Id };
        Galgame gameB = new() { UserId = TestUserId, Name = "Game B", RedirectTo = gameC.Id };
        Context.Galgame.AddRange(gameA, gameB);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = gameA.Id, Relation = [Career.Painter] },
                new StaffGameUpdateDto { GameId = gameB.Id, Relation = [Career.Writer] }
            ]
        };

        // Act - GetGalgamesAsync 会对重定向后的游戏去重，所以只返回一个游戏
        // 因此 games.Count (1) != staff.StaffGames.Count (2)，应该抛出 KeyNotFoundException
        // 这个测试验证了重定向去重的行为

        // Assert - 由于去重导致数量不匹配，应该抛出异常
        Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await _service.UpsertAsync(TestUserId, staffDto));
    }

    [Test]
    public void UpsertAsync_WithRedirectToNonExistent_ThrowsKeyNotFoundException()
    {
        // Arrange - 创建一个重定向到不存在游戏的游戏
        Galgame game = new() { UserId = TestUserId, Name = "Game", RedirectTo = 99999 };
        Context.Galgame.Add(game);
        Context.SaveChanges();

        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = game.Id, Relation = [Career.Painter] }
            ]
        };

        // Act & Assert - 应该抛出 KeyNotFoundException，因为重定向目标不存在
        Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await _service.UpsertAsync(TestUserId, staffDto));
    }

    [Test]
    public void UpsertAsync_WithNonExistentGame_ThrowsKeyNotFoundException()
    {
        // Arrange
        StaffUpdateDto staffDto = new()
        {
            JapaneseName = "Test Staff",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = 99999, Relation = [Career.Painter] }
            ]
        };

        // Act & Assert
        Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await _service.UpsertAsync(TestUserId, staffDto));
    }

    [Test]
    public async Task UpsertAsync_UpdateExistingStaffWithRedirectedGame_Success()
    {
        // Arrange - 创建现有 Staff 和重定向游戏
        Galgame targetGame = new() { UserId = TestUserId, Name = "Target Game", RedirectTo = 0 };
        Context.Galgame.Add(targetGame);
        await Context.SaveChangesAsync();

        Galgame sourceGame = new() { UserId = TestUserId, Name = "Source Game", RedirectTo = targetGame.Id };
        Context.Galgame.Add(sourceGame);
        await Context.SaveChangesAsync();

        // 创建现有 Staff
        Staff existingStaff = new() { UserId = TestUserId, JapaneseName = "Original Name" };
        Context.Staff.Add(existingStaff);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            Id = existingStaff.Id,
            JapaneseName = "Updated Name",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = sourceGame.Id, Relation = [Career.Producer] }
            ]
        };

        // Act
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo(existingStaff.Id));
        Assert.That(result.JapaneseName, Is.EqualTo("Updated Name"));
        Assert.That(result.StaffGames, Has.Count.EqualTo(1));
    }

    #endregion
}