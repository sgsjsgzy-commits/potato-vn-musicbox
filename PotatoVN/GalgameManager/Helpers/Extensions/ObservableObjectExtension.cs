using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;

namespace GalgameManager.Helpers;

public static class ObservableObjectExtension
{
    /// <summary>
    /// 通过序列化和反序列化实现深拷贝
    /// </summary>
    /// <param name="obj"></param>
    /// <param name="settings"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T DeepClone<T>(this T obj, JsonSerializerSettings? settings = null) where T : ObservableObject
    {
        var json = JsonConvert.SerializeObject(obj, settings:settings);
        return JsonConvert.DeserializeObject<T>(json, settings)!;
    }
}