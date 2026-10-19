using GalgameManager.Server.Contracts;
using GalgameManager.Server.Enums;
using GalgameManager.Server.Models;
using GalgameManager.Server.Repositories;
using GalgameManager.Server.Services;
using Moq;

namespace GalgameManager.Server.Test.Services;

/// <summary>
/// StaffService StaffGame 增量同步测试
/// 测试更新已有 Staff 的 StaffGames 时，不会因 EF ChangeTracker 跟踪冲突而抛出 InvalidOperationException
/// </summary>
[TestFixture]
public class StaffServiceStaffGameSyncTests : TestBase
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

    #region Update existing Staff - StaffGame sync without tracking conflict

    [Test]
    public async Task UpsertAsync_UpdateExistingStaff_KeepSameStaffGame_NoTrackingConflict()
    {
        // Arrange - 创建游戏和已有 Staff（带一个 StaffGame）
        Galgame game1 = new() { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        Context.Galgame.Add(game1);
        await Context.SaveChangesAsync();

        Staff existingStaff = new() { UserId = TestUserId, JapaneseName = "Original" };
        existingStaff.StaffGames.Add(new StaffGame
        {
            StaffId = 0, // 会在 SaveChanges 后由 EF 填充
            GameId = game1.Id,
            Relation = [Career.Writer],
            Staff = existingStaff,
            Game = game1
        });
        Context.Staff.Add(existingStaff);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            Id = existingStaff.Id,
            JapaneseName = "Updated",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = game1.Id, Relation = [Career.Painter] }
            ]
        };

        // Act - 更新已有 Staff，保持同一个 StaffGame（只改 Relation），不应抛出跟踪冲突异常
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(1));
        Assert.That(result.StaffGames[0].GameId, Is.EqualTo(game1.Id));
        Assert.That(result.StaffGames[0].Relation, Is.EqualTo(new List<Career> { Career.Painter }));
    }

    [Test]
    public async Task UpsertAsync_UpdateExistingStaff_AddNewStaffGame_NoTrackingConflict()
    {
        // Arrange - 创建游戏和已有 Staff（带一个 StaffGame）
        Galgame game1 = new() { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        Galgame game2 = new() { UserId = TestUserId, Name = "Game 2", RedirectTo = 0 };
        Context.Galgame.AddRange(game1, game2);
        await Context.SaveChangesAsync();

        Staff existingStaff = new() { UserId = TestUserId, JapaneseName = "Original" };
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game1.Id,
            Relation = [Career.Writer],
            Staff = existingStaff,
            Game = game1
        });
        Context.Staff.Add(existingStaff);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            Id = existingStaff.Id,
            JapaneseName = "Updated",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = game1.Id, Relation = [Career.Writer] },
                new StaffGameUpdateDto { GameId = game2.Id, Relation = [Career.Musician] }
            ]
        };

        // Act - 在已有 StaffGame 基础上新增一个，不应抛出跟踪冲突异常
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(2));
        Assert.That(result.StaffGames.Any(sg => sg.GameId == game1.Id), Is.True);
        Assert.That(result.StaffGames.Any(sg => sg.GameId == game2.Id), Is.True);
    }

    [Test]
    public async Task UpsertAsync_UpdateExistingStaff_RemoveStaffGame_NoTrackingConflict()
    {
        // Arrange - 创建游戏和已有 Staff（带两个 StaffGame）
        Galgame game1 = new() { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        Galgame game2 = new() { UserId = TestUserId, Name = "Game 2", RedirectTo = 0 };
        Context.Galgame.AddRange(game1, game2);
        await Context.SaveChangesAsync();

        Staff existingStaff = new() { UserId = TestUserId, JapaneseName = "Original" };
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game1.Id, Relation = [Career.Writer], Staff = existingStaff, Game = game1
        });
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game2.Id, Relation = [Career.Painter], Staff = existingStaff, Game = game2
        });
        Context.Staff.Add(existingStaff);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            Id = existingStaff.Id,
            JapaneseName = "Updated",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = game1.Id, Relation = [Career.Writer] }
            ]
        };

        // Act - 只保留 game1，删除 game2，不应抛出跟踪冲突异常
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(1));
        Assert.That(result.StaffGames[0].GameId, Is.EqualTo(game1.Id));
    }

    [Test]
    public async Task UpsertAsync_UpdateExistingStaff_ReplaceAllStaffGames_NoTrackingConflict()
    {
        // Arrange - 创建游戏和已有 Staff（带两个 StaffGame）
        Galgame game1 = new() { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        Galgame game2 = new() { UserId = TestUserId, Name = "Game 2", RedirectTo = 0 };
        Galgame game3 = new() { UserId = TestUserId, Name = "Game 3", RedirectTo = 0 };
        Context.Galgame.AddRange(game1, game2, game3);
        await Context.SaveChangesAsync();

        Staff existingStaff = new() { UserId = TestUserId, JapaneseName = "Original" };
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game1.Id, Relation = [Career.Writer], Staff = existingStaff, Game = game1
        });
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game2.Id, Relation = [Career.Painter], Staff = existingStaff, Game = game2
        });
        Context.Staff.Add(existingStaff);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            Id = existingStaff.Id,
            JapaneseName = "Updated",
            StaffGames =
            [
                new StaffGameUpdateDto { GameId = game2.Id, Relation = [Career.Musician] },
                new StaffGameUpdateDto { GameId = game3.Id, Relation = [Career.Producer] }
            ]
        };

        // Act - 删除 game1，保留 game2（更新 Relation），新增 game3
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Has.Count.EqualTo(2));
        Assert.That(result.StaffGames.Any(sg => sg.GameId == game2.Id), Is.True);
        Assert.That(result.StaffGames.Any(sg => sg.GameId == game3.Id), Is.True);
        // game2 的 Relation 应被更新
        StaffGame game2Sg = result.StaffGames.First(sg => sg.GameId == game2.Id);
        Assert.That(game2Sg.Relation, Is.EqualTo(new List<Career> { Career.Musician }));
    }

    [Test]
    public async Task UpsertAsync_UpdateExistingStaff_ClearAllStaffGames_NoTrackingConflict()
    {
        // Arrange - 创建游戏和已有 Staff（带两个 StaffGame）
        Galgame game1 = new() { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        Galgame game2 = new() { UserId = TestUserId, Name = "Game 2", RedirectTo = 0 };
        Context.Galgame.AddRange(game1, game2);
        await Context.SaveChangesAsync();

        Staff existingStaff = new() { UserId = TestUserId, JapaneseName = "Original" };
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game1.Id, Relation = [Career.Writer], Staff = existingStaff, Game = game1
        });
        existingStaff.StaffGames.Add(new StaffGame
        {
            GameId = game2.Id, Relation = [Career.Painter], Staff = existingStaff, Game = game2
        });
        Context.Staff.Add(existingStaff);
        await Context.SaveChangesAsync();

        StaffUpdateDto staffDto = new()
        {
            Id = existingStaff.Id,
            JapaneseName = "Updated",
            StaffGames = []
        };

        // Act - 清空所有 StaffGame
        Staff result = await _service.UpsertAsync(TestUserId, staffDto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.StaffGames, Is.Empty);
    }

    [Test]
    public async Task UpsertAsync_UpdateExistingStaff_MultipleTimes_NoTrackingConflict()
    {
        // Arrange - 模拟客户端多次上传同一个 Staff（包含相同 StaffGame）
        Galgame game1 = new() { UserId = TestUserId, Name = "Game 1", RedirectTo = 0 };
        Context.Galgame.Add(game1);
        await Context.SaveChangesAsync();

        // 第一次上传：新建 Staff
        StaffUpdateDto dto1 = new()
        {
            JapaneseName = "Test Staff",
            StaffGames = [new StaffGameUpdateDto { GameId = game1.Id, Relation = [Career.Writer] }]
        };
        Staff result1 = await _service.UpsertAsync(TestUserId, dto1);
        Assert.That(result1.StaffGames, Has.Count.EqualTo(1));

        // 第二次上传：更新同一个 Staff，保持相同 StaffGame
        StaffUpdateDto dto2 = new()
        {
            Id = result1.Id,
            JapaneseName = "Test Staff Updated",
            StaffGames = [new StaffGameUpdateDto { GameId = game1.Id, Relation = [Career.Writer] }]
        };

        // Act
        Staff result2 = await _service.UpsertAsync(TestUserId, dto2);

        // Assert - 第二次更新不应抛出跟踪冲突
        Assert.That(result2, Is.Not.Null);
        Assert.That(result2.JapaneseName, Is.EqualTo("Test Staff Updated"));
        Assert.That(result2.StaffGames, Has.Count.EqualTo(1));
        Assert.That(result2.StaffGames[0].GameId, Is.EqualTo(game1.Id));

        // 第三次上传：再次更新同一个 Staff
        StaffUpdateDto dto3 = new()
        {
            Id = result2.Id,
            JapaneseName = "Test Staff Updated Again",
            StaffGames = [new StaffGameUpdateDto { GameId = game1.Id, Relation = [Career.Painter] }]
        };

        Staff result3 = await _service.UpsertAsync(TestUserId, dto3);
        Assert.That(result3.StaffGames, Has.Count.EqualTo(1));
        Assert.That(result3.StaffGames[0].Relation, Is.EqualTo(new List<Career> { Career.Painter }));
    }

    #endregion
}