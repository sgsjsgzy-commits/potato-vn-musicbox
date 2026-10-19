using GalgameManager.Server.Contracts;
using GalgameManager.Server.Data;
using GalgameManager.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Server.Repositories;

public class StaffRepository(DataContext dbContext) : IStaffRepository
{
    public async Task<Staff> GetStaffWithIdAsync(int staffId)
    {
        Staff? staff = await dbContext.Staff
            .Include(s => s.StaffGames)
            .FirstOrDefaultAsync(s => s.Id == staffId);
        if (staff is null) throw new KeyNotFoundException($"staff with id {staffId} is not founded.");
        return staff;
    }

    public Task<List<Staff>> GetStaffsAsync(int gameId)
    {
        return dbContext.StaffGame
            .Where(sg => sg.GameId == gameId )
            .Include(sg => sg.Staff).ThenInclude(s => s.StaffGames)
            .Select(sg => sg.Staff)
            .Where(s => !s.IsDeleted)
            .ToListAsync();
    }

    public async Task<(List<Staff> staffs, int cnt)> GetStaffsAsync(int userId, long lastChangedTimeStamp,
        int pageIndex, int pageSize, bool includeDeleted = true)
    {
        IQueryable<Staff> query = dbContext.Staff
            .Include(sg => sg.StaffGames)
            .Where(s => s.UserId == userId)
            .Where(s => s.LastModifyTimestamp > lastChangedTimeStamp);
        if (!includeDeleted) query = query.Where(s => !s.IsDeleted);
        var cnt = await query.CountAsync();
        return (await query.Skip(pageIndex * pageSize).Take(pageSize).ToListAsync(), cnt);
    }

    public async Task<Staff> GetStaffAsync(int staffId)
    {
        Staff? staff = await dbContext.Staff
            .Include(s => s.StaffGames)
            .FirstOrDefaultAsync(s => s.Id == staffId);
        if (staff is null) throw new KeyNotFoundException();
        return staff;
    }

    public async Task<Staff> Upsert(Staff staff)
    {
        dbContext.Staff.Update(staff);
        await dbContext.SaveChangesAsync();
        return staff;
    }

    public Task Delete(Staff staff)
    {
        dbContext.Staff.Remove(staff);
        return dbContext.SaveChangesAsync();
    }
}