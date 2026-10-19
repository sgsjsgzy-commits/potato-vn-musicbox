using GalgameManager.Server.Models;

namespace GalgameManager.Server.Contracts;

public interface ICharacterRepository
{
    /// <summary>
    /// 获取某个游戏的所有角色，若什么都没有则返回空列表
    /// </summary>
    /// <param name="gameId"></param>
    /// <returns></returns>
    public Task<List<Character>> GetCharactersAsync(int gameId);
    
    /// <summary>
    /// 更新某个游戏的角色列表
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="gameId"></param>
    /// <param name="characters"></param>
    /// <returns></returns>
    public Task UpdateCharacterAsync(int userId, int gameId, List<Character> characters);
}