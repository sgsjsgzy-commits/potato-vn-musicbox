using System;
using GalgameManager.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Server.Test;

/// <summary>
/// 测试基类，提供内存数据库支持
/// </summary>
public abstract class TestBase
{
    protected DataContext Context { get; private set; } = null!;

    [SetUp]
    public virtual void Setup()
    {
        var options = new DbContextOptionsBuilder<DataContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        Context = new DataContext(options);
        Context.Database.EnsureCreated();
    }

    [TearDown]
    public virtual void TearDown()
    {
        Context.Database.EnsureDeleted();
        Context.Dispose();
    }
}
