using CommunityToolkit.Mvvm.Messaging.Messages;
using GalgameManager.Models;

namespace GalgameManager.WinApp.Base.Models.Msgs;

public class GalgamePlayedMessage(Galgame galgame) : ValueChangedMessage<Galgame>(galgame);
public class GalgameStoppedMessage(Galgame galgame) : ValueChangedMessage<Galgame>(galgame);
