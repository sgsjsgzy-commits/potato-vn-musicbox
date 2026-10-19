using GalgameManager.Server.Models;

namespace GalgameManager.Server.Contracts;

public interface IStaffService
{
    public Task<PagedResult<Staff>> GetStaffsAsync(int userId, long lastChanged, int pageIndex, int pageSize,
        bool includeDeleted = true);

    /// <summary>
    /// 添加或更新staff
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="dto"></param>
    /// <returns></returns>
    /// <exception cref="KeyNotFoundException">payload中填入了id但找不到该staff 或 payload中staff的某些游戏找不到</exception>
    public Task<Staff> UpsertAsync(int userId, StaffUpdateDto dto);
}