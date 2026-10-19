using GalgameManager.Server.Contracts;
using GalgameManager.Server.Models;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace GalgameManager.Server.Services;

public class OssService(
    IMinioClient client,
    IConfiguration config,
    IUserRepository userRepository,
    IOssRecordRepository ossRecordRepository) : IOssService
{
    public string BucketName { get; } = config["AppSettings:Minio:BucketName"] ?? "potatovn";

    public long SpacePerUser { get; } = config["AppSettings:User:OssSize"] is null
        ? 104857600 // 100MB
        : Convert.ToInt64(config["AppSettings:User:OssSize"]);
    
    public string GetFullKey(int userId, string objectFullName) => $"{userId}/{objectFullName}";

    public async Task<string?> GetWritePresignedUrlAsync(int userId, string objectFullName, long requireSpace)
    {
        if (string.IsNullOrEmpty(objectFullName)) return null;
        try
        {
            User? user = await userRepository.GetUserAsync(userId);
            if (user is null) throw new ArgumentException("User not found"); //不应该发生
            var space = Math.Min(user.TotalSpace - user.UsedSpace, requireSpace);
            Dictionary<string, string> headers = new()
            {
                { "Content-Length", space.ToString() }, // 限制文件大小
            };
            // 预先占用空间，等待上传完成后客户端调用接口以更新实际使用空间
            // 预先占用空间为 Max(要替换文件的大小（如有）, 预分配大小)，防止用新上传的小文件且故意不Update来欺骗占用空间
            OssRecord? record = await ossRecordRepository.GetRecordByKeyAsync(GetFullKey(userId, objectFullName));
            await UpdateUserUsedSpaceAsync(new ObjectEntity
                { Key = GetFullKey(userId, objectFullName), Size = Math.Max(space, record?.Size ?? 0) });
            return await client.PresignedPutObjectAsync(new PresignedPutObjectArgs()
                .WithBucket(BucketName)
                .WithObject(GetFullKey(userId, objectFullName))
                .WithExpiry(10 * 60)
                .WithHeaders(headers));
        }
        catch (Exception e)
        {
            if (e is InvalidObjectNameException)
                return null;
            throw;
        }
    }

    public async Task<string?> GetReadPresignedUrlAsync(int userId, string? objectFullName)
    {
        if (string.IsNullOrEmpty(objectFullName)) return null;
        try
        {
            return await client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                .WithBucket(BucketName)
                .WithObject(GetFullKey(userId, objectFullName))
                .WithExpiry(604800)); // 7 days
        }
        catch (Exception e)
        {
            if (e is InvalidObjectNameException or ObjectNotFoundException)
                return null;
            throw;
        }
    }

    public async Task<ObjectEntity?> GetObjectAsync(int userId, string objectFullName)
    {
        try
        {
            ObjectStat stat = await client.StatObjectAsync(new StatObjectArgs().WithBucket(BucketName)
                .WithObject(GetFullKey(userId, objectFullName)));
            return new ObjectEntity { Key = GetFullKey(userId, objectFullName), Size = stat.Size };
        }
        catch (MinioException) // ObjectNotFoundException
        {
            return null;
        }
    }

    public async Task DeleteObjectAsync(int userId, string? objectFullName)
    {
        if (string.IsNullOrEmpty(objectFullName)) return;
        await client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(BucketName)
            .WithObject(GetFullKey(userId, objectFullName)));
        await UpdateUserUsedSpaceAsync(new ObjectEntity { Key = GetFullKey(userId, objectFullName), Size = 0 });
    }

    public async Task UpdateUserUsedSpaceAsync(ObjectEntity entity)
    {
        if (entity.Key.Contains('/') == false) return;
        var userId = Convert.ToInt32(entity.Key.Split('/')[0]);
        User? user = await userRepository.GetUserAsync(userId);
        if (user is null) return;
        
        user.UsedSpace += await ossRecordRepository.UpdateRecordAsync(user.Id, entity.Key, entity.Size);
        await userRepository.UpdateUserAsync(user);
    }
}