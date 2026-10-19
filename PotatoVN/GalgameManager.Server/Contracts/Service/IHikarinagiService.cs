using GalgameManager.Server.Models;

namespace GalgameManager.Server.Contracts;

public interface IHikarinagiService
{
    public bool IsEnable { get; }

    /// <summary>
    /// 透传GET请求至Hikarinagi开放API，自动附加访问令牌，需要在外部捕获异常
    /// </summary>
    /// <param name="path">API路径（相对于/api/v3/open/），如 galgames/1</param>
    /// <param name="query">原始查询字符串（含前导?），可为空字符串</param>
    /// <returns>上游响应的状态码、响应正文与ContentType</returns>
    public Task<ScraperProxyResult> ProxyAsync(string path, string query);
}
