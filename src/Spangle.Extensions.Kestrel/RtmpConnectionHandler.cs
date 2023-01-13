﻿using Cysharp.Text;
using Microsoft.AspNetCore.Connections;
using Spangle.Rtmp;

namespace Spangle.Extensions.Kestrel;

public class RtmpConnectionHandler : ConnectionHandler
{
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var rtmp = new RtmpReceiver();
        await rtmp.BeginReadAsync(connection.GetIdentifier(), connection.Transport);
    }
}
