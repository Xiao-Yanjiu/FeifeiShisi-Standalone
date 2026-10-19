// SPDX-License-Identifier: GPL-3.0-or-later
using System;using System.Collections.Generic;
namespace OriginalDesktop;
internal class ModuleSettings
{
    public List<string> PageOrder { get; set; } = new();
    public List<string> DisabledPages { get; set; } = new();
    public int Port { get; set; } = 18765;
    public string StreamAdapter { get; set; } = "";
    public bool StreamGpuDirect { get; set; } = true;
    public string StreamTransport { get; set; } = "tcp";
    public int StreamUdpPort { get; set; } = 0;
    public int StreamUdpExternalPort { get; set; } = 0;
    public bool StreamBringToFront { get; set; } = true;
    public bool HardwareStream { get; set; } = true;
    public string StreamEncoderPath { get; set; } = "";
    public string StreamHardwareHelper { get; set; } = "";
    public string PublicAddress { get; set; } = "";
    public int ExternalPort { get; set; } = 18765;
    public bool ReceiveTell { get; set; } = true;
    public bool AllowCommands { get; set; } = false;
    public bool SyncAllMessages { get; set; } = true;
    public bool NotifyParty { get; set; } = true;
    public bool NotifyTrade { get; set; } = true;
    public bool NotifyFriend { get; set; } = true;
    public bool NotifyTarget { get; set; } = false;
    public int PushMode { get; set; } = 0;
    public int[] PushPriority { get; set; }
    public int PushProvider { get; set; } = -1;
    public bool QQNotifications { get; set; }
    public string QQBotAccount { get; set; } = "";
    public string QQNotifyTarget { get; set; } = "";
    public bool QQNotifyGroup { get; set; }
    public string QQMention { get; set; } = "";
    public string QQGroups { get; set; } = "";
    public bool QQMirror { get; set; }
    public bool QQReply { get; set; }
    public string QQReplyUsers { get; set; } = "";
    public string QQReplyChannel { get; set; } = "小队";
    public bool QQAutoConnect { get; set; }
    public List<string> QQChannels { get; set; } = new();
    public int QQSyncSchema { get; set; }
    public bool KeepDirectNotifications { get; set; } = false;
    public List<string> Channels { get; set; } = new()
    {
        "小队",
        "团队",
        "部队",
        "悄悄话",
        "提醒"
    };
}