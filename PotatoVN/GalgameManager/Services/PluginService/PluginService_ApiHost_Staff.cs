using GalgameManager.Contracts.Services;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;

namespace GalgameManager.Services;

public partial class PluginService
{
    public partial class PotatoVnApiHost : IPotatoVnApi
    {
        private readonly IStaffService _staffService = App.GetService<IStaffService>();

        public Staff? GetStaff(Guid? id) => _staffService.GetStaff(id);

        public List<Staff> GetStaffs() => _staffService.GetStaffs();

        public List<Staff> GetStaffs(Galgame game) => _staffService.GetStaffs(game);

        public void SaveStaff(Staff staff, bool sync = true) => _staffService.Save(staff, sync);
    }
}
