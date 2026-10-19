using GalgameManager.Server.Contracts;
using GalgameManager.Server.Helpers;
using GalgameManager.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GalgameManager.Server.Controllers;

[Route("[controller]")]
[ApiController]
public class OssController(IOssService ossService, IUserRepository userRepository)
    : ControllerBase
{
    /// <summary>获取oss预签名上传路径， 获取链接后会预先占用请求的空间， 直到客户端使用UpdateSpace通知服务器更新空间</summary>
    /// <param name="objectFullName">上传文件名（包括前缀），如：Galgame/114514.jpg</param>
    /// <param name="requireSpace">要上传的文件大小（byte），默认为10MB</param>
    /// <response code="400">objectFullName不合法 或 用户存储容量已满</response>
    [HttpGet("put")]
    [Authorize]
    public async Task<ActionResult<string>> GetPutPresignedUrl(string objectFullName = "",
        long requireSpace = 10 * 1024 * 1024)
    {
        User? user = await userRepository.GetUserAsync(this.GetUserId());
        if (user is null) return BadRequest("User not found");
        if (user.UsedSpace >= user.TotalSpace) return BadRequest("User used space exceed max space");
        var result = await ossService.GetWritePresignedUrlAsync(this.GetUserId(), objectFullName, requireSpace);
        if (result is null)
            return BadRequest("Invalid object name");
        return Ok(result);
    }

    /// <summary>获取oss预签名读取路径</summary>
    /// <param name="objectFullName">上传文件名（包括前缀），如：Galgame/114514.jpg</param>
    /// <response code="404">文件不存在</response>
    [HttpGet("get")]
    [Authorize]
    public async Task<ActionResult<string>> GetGetPresignedUrl(string objectFullName = "")
    {
        var result = await ossService.GetReadPresignedUrlAsync(this.GetUserId(), objectFullName);
        if (result is null)
            return NotFound("Object not found");
        return Ok(result);
    }

    /// <summary>
    /// 更新某个key下文件占用空间，一般用于上传完成后通知服务器计算具体空间
    /// </summary>
    /// <remarks>
    /// 更新某个key下文件所占用的空间<br/>
    /// <b>即使是上传失败/取消，也需要调用此接口以释放预占用的空间</b>
    /// </remarks>
    /// <response code="400">token对应的用户不存在</response>
    [HttpPut("update")]
    [Authorize]
    public async Task<ActionResult<UserDto>> UpdateSpace(string objectFullName = "")
    {
        User? user = await userRepository.GetUserAsync(this.GetUserId());
        if (user is null) return BadRequest("User not found");
        ObjectEntity entity = await ossService.GetObjectAsync(user.Id, objectFullName) ?? new ObjectEntity
            { Key = ossService.GetFullKey(user.Id, objectFullName), Size = 0 };
        await ossService.UpdateUserUsedSpaceAsync(entity);
        return Ok(new UserDto(user));
    }
}