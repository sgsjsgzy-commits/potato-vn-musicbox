using GalgameManager.Contracts.Services;
using GalgameManager.Helpers;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Models.Filters;

namespace GalgameManager.Services;

public partial class PluginService
{
    public partial class PotatoVnApiHost : IPotatoVnApi
    {
        private readonly IFilterService _filterService = App.GetService<IFilterService>();

        public void AddFilter(FilterBase filter)
            => UiThreadInvokeHelper.Invoke(() => _filterService.AddFilter(filter));

        public void DeleteFilter(FilterBase filter)
            => UiThreadInvokeHelper.Invoke(() => _filterService.RemoveFilter(filter));

        public void ClearFilters()
            => UiThreadInvokeHelper.Invoke(() => _filterService.ClearFilters());

        public async Task<List<FilterBase>> GetFiltersAsync()
        {
            List<FilterBase> result = new();
            await UiThreadInvokeHelper.InvokeAsync(() => result = _filterService.GetFilters().ToList());
            return result;
        }
    }
}
