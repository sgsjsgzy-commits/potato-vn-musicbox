using System.ComponentModel.DataAnnotations;
using AutoMapper;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Helpers;
using GalgameManager.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GalgameManager.Server.Controllers;

[ApiController]
[Route("[controller]")]
public class StaffController (IStaffService staffService, IOssService ossService, IMapper mapper) : ControllerBase
{
    /// <summary>获取staff列表</summary>
    /// <remarks>
    /// 获取最后一次更新时间严格晚于给定时间戳的staff列表<br/>
    /// 若includeDeleted=true，被删除的staff也会返回，删除的staff的IsDeleted字段为true
    /// </remarks>
    /// <response code="400">pageIndex小于0或pageSize小于等于0</response>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<PagedResult<StaffDto>>> GetStaffsAsync([FromQuery][Required] long timestamp,
        [FromQuery] int pageIndex = 0, [FromQuery] int pageSize = 10, [FromQuery] bool includeDeleted = true) 
    {
        if (pageIndex < 0 || pageSize <= 0) return BadRequest("Invalid pageIndex or pageSize.");
        var userId = this.GetUserId();
        PagedResult<Staff> tmp = await staffService.GetStaffsAsync(userId, timestamp, pageIndex, pageSize, includeDeleted);
        List<StaffDto> dtos = [];
        foreach (Staff s in tmp.Items) dtos.Add(await ToDto(s));
        PagedResult<StaffDto> result = new(dtos, tmp.PageIndex, tmp.PageSize, tmp.Cnt);
        return Ok(result);
    }
    
    /// <summary>新建或更新staff</summary>
    /// <remarks>
    /// 所有字段均可选，覆盖原字段 <br/>
    /// <b>若Id没有填或为0，则认为是新建staff</b> <br/>
    /// 其中IsDelete表示是否要删除这个staff<br/>
    /// </remarks>
    /// <response code="404">填入了id字段，但不存在具有该id的staff</response>
    /// <response code="400">调用方不是该Staff所属者</response>
    [HttpPatch]
    [Authorize]
    public async Task<ActionResult<StaffDto>> AddOrUpdateStaffAsync([FromBody] StaffUpdateDto payload)
    {
        var userId = this.GetUserId();
        try
        {
            Staff staff = await staffService.UpsertAsync(userId, payload);
            return Ok(await ToDto(staff));
        }
        catch (KeyNotFoundException e)
        {
            return NotFound(e.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest($"You are not the owner of the staff {payload.Id}.");
        }
    }


    private async Task<StaffDto> ToDto(Staff staff)
    {
        StaffDto result = mapper.Map<StaffDto>(staff);
        result.ImageUrl = await ossService.GetReadPresignedUrlAsync(this.GetUserId(), staff.ImageLoc);
        return result;
    }
}