using GalgameManager.Server.Models;

namespace GalgameManager.Server.Contracts;

public interface IStaffRepository
{
    /// <summary>
    /// 获取staff
    /// </summary>
    /// <param name="staffId"></param>
    /// <exception cref="KeyNotFoundException">该id的staff不存在</exception>
    /// <returns></returns>
    public Task<Staff> GetStaffWithIdAsync(int staffId);
    
    /// <summary>
    /// 获取某游戏的所有staff（不包括被删除的staff），若游戏不存在则返回空列表
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public Task<List<Staff>> GetStaffsAsync(int gameId);

    /// <summary>
    /// 获取某用户的所有staff（包括被删除的staff）
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="lastChangedTimeStamp">获取修改时间晚于此的游戏</param>
    /// <param name="pageIndex"></param>
    /// <param name="pageSize"></param>
    /// <param name="includeDeleted"></param>
    /// <returns></returns>
    public Task<(List<Staff> staffs, int cnt)> GetStaffsAsync(int userId, long lastChangedTimeStamp, int pageIndex,
        int pageSize, bool includeDeleted = true);
    
    /// <summary>
    /// 获取某staff的详细信息
    /// </summary>
    /// <param name="staffId"></param>
    /// <exception cref="KeyNotFoundException">staff不存在</exception>
    public Task<Staff> GetStaffAsync(int staffId);

    /// <summary>
    /// 插入/更新staff
    /// </summary>
    /// <param name="staff"></param>
    /// <returns></returns>
    public Task<Staff> Upsert(Staff staff);

    /// <summary>
    /// 硬删除一个游戏，如果不是为了回滚数据库不要这么做，使用upsert软删除
    /// </summary>
    /// <param name="staff"></param>
    /// <returns></returns>
    public Task Delete(Staff staff);
}