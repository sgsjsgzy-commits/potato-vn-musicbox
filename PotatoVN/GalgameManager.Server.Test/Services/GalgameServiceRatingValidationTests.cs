using AutoMapper;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Models;
using GalgameManager.Server.Repositories;
using GalgameManager.Server.Services;
using Moq;

namespace GalgameManager.Server.Test.Services;

/// <summary>
/// GalgameService Rating 字段校验测试
/// 当上传的 Rating 为 NaN / Infinity 时，应忽略该字段（保留原值），符合 PATCH 仅覆盖已上传合法字段的语义
/// </summary>
[TestFixture]
public class GalgameServiceRatingValidationTests : TestBase
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

    #region Update existing game - invalid Rating should be ignored

    [Test]
    public async Task AddOrUpdateGalgameAsync_NaNRating_ShouldKeepOriginalRating()
    {
        // Arrange
        Galgame existingGame = new()
        {
            UserId = TestUserId,
            BgmId = "12345",
            Name = "Existing Game",
            Rating = 8.5f
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        GalgameUpdateDto updateDto = new()
        {
            Id = existingGame.Id,
            Rating = float.NaN
        };

        // Act
        Galgame result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - NaN 应被忽略，保留原值
        Assert.That(result.Rating, Is.EqualTo(8.5f));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_PositiveInfinityRating_ShouldKeepOriginalRating()
    {
        // Arrange
        Galgame existingGame = new()
        {
            UserId = TestUserId,
            BgmId = "12345",
            Name = "Existing Game",
            Rating = 7.0f
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        GalgameUpdateDto updateDto = new()
        {
            Id = existingGame.Id,
            Rating = float.PositiveInfinity
        };

        // Act
        Galgame result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - Infinity 应被忽略，保留原值
        Assert.That(result.Rating, Is.EqualTo(7.0f));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NegativeInfinityRating_ShouldKeepOriginalRating()
    {
        // Arrange
        Galgame existingGame = new()
        {
            UserId = TestUserId,
            BgmId = "12345",
            Name = "Existing Game",
            Rating = 6.0f
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        GalgameUpdateDto updateDto = new()
        {
            Id = existingGame.Id,
            Rating = float.NegativeInfinity
        };

        // Act
        Galgame result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - -Infinity 应被忽略，保留原值
        Assert.That(result.Rating, Is.EqualTo(6.0f));
    }

    #endregion

    #region Update existing game - valid/null Rating should behave correctly

    [Test]
    public async Task AddOrUpdateGalgameAsync_ValidRating_ShouldUpdateRating()
    {
        // Arrange
        Galgame existingGame = new()
        {
            UserId = TestUserId,
            BgmId = "12345",
            Name = "Existing Game",
            Rating = 5.0f
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        GalgameUpdateDto updateDto = new()
        {
            Id = existingGame.Id,
            Rating = 9.5f
        };

        // Act
        Galgame result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - 合法值应正常更新
        Assert.That(result.Rating, Is.EqualTo(9.5f));
    }

    [Test]
    public async Task AddOrUpdateGalgameAsync_NullRating_ShouldKeepOriginalRating()
    {
        // Arrange
        Galgame existingGame = new()
        {
            UserId = TestUserId,
            BgmId = "12345",
            Name = "Existing Game",
            Rating = 4.0f
        };
        Context.Galgame.Add(existingGame);
        await Context.SaveChangesAsync();

        GalgameUpdateDto updateDto = new()
        {
            Id = existingGame.Id,
            Rating = null
        };

        // Act
        Galgame result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - null 应被忽略，保留原值
        Assert.That(result.Rating, Is.EqualTo(4.0f));
    }

    #endregion

    #region New game - invalid Rating should fall back to default

    [Test]
    public async Task AddOrUpdateGalgameAsync_NaNRatingOnNewGame_ShouldKeepDefaultRating()
    {
        // Arrange - 新建游戏时传入 NaN Rating
        GalgameUpdateDto updateDto = new()
        {
            Id = null,
            BgmId = "12345",
            Name = "New Game",
            Rating = float.NaN
        };

        // Act
        Galgame result = await _service.AddOrUpdateGalgameAsync(TestUserId, updateDto);

        // Assert - NaN 应被忽略，使用默认值 0
        Assert.That(result.Rating, Is.EqualTo(0f));
    }

    #endregion
}