using GalgameManager.Server.Contracts;
using GalgameManager.Server.Data;
using GalgameManager.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Server.Repositories;

public class CharacterRepository(DataContext context, IOssService oss) : ICharacterRepository
{
    public Task<List<Character>> GetCharactersAsync(int gameId)
    {
        return context.Character.Where(c => c.GalgameId == gameId).ToListAsync();
    }

    public async Task UpdateCharacterAsync(int userId, int gameId, List<Character> characters)
    {
        List<Character> oldCharacters = new(await GetCharactersAsync(gameId));
        HashSet<Character> newList = [];
        foreach (Character c in characters)
        {
            Character? old = oldCharacters.FirstOrDefault(oc => oc.Id == c.Id || oc.Name == c.Name);
            if (old is not null)
            {
                c.Id = old.Id;
                if (!string.IsNullOrEmpty(c.ImageLoc) && c.ImageLoc != old.ImageLoc) 
                    await oss.DeleteObjectAsync(userId, old.ImageLoc);
                if (!string.IsNullOrEmpty(c.PreviewImageLoc) && c.PreviewImageLoc != old.PreviewImageLoc)
                    await oss.DeleteObjectAsync(userId, old.PreviewImageLoc);
                context.Entry(old).CurrentValues.SetValues(c);
            }
            else
                context.Character.Update(c);
            newList.Add(old ?? c);
        }

        foreach (Character c in oldCharacters.Where(c => !newList.Contains(c)))
        {
            await oss.DeleteObjectAsync(userId, c.ImageLoc);
            await oss.DeleteObjectAsync(userId, c.PreviewImageLoc);
            context.Character.Remove(c);
        }
        
        await context.SaveChangesAsync();
    }
}