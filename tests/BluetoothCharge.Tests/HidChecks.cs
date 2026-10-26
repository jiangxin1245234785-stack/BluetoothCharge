using BluetoothCharge;

internal static class HidChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name); count++;
        }
        Check(LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 0xff00, 1, 7, 7), "C54D vendor short-report collection allowed");
        Check(LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 0xff00, 2, 20, 20), "C54D vendor long-report collection allowed");
        Check(!LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 1, 2, 20, 20), "main mouse collection never opened for vendor queries");
        Check(!LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 1, 6, 20, 20), "main keyboard collection never opened for vendor queries");
        Check(!LogitechHidBattery.IsSupportedCollection(0x046d, 0xc52b, 0xff00, 2, 20, 20), "unimplemented receiver PID excluded");
        Check(!LogitechHidBattery.IsSupportedCollection(0x0001, 0xc54d, 0xff00, 2, 20, 20), "unrelated vendor excluded");
        Check(!LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 0xff00, 2, 2, 2), "truncated HID collection excluded");
        Check(!LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 0xff00, 1, 20, 20)
            && !LogitechHidBattery.IsSupportedCollection(0x046d, 0xc54d, 0xff00, 2, 7, 7), "short and long collection lengths cannot be interchanged");
        Check(LogitechHidBattery.IsSupportedReceiverInstance("USB\\VID_046D&PID_C54D\\fixture")
            && LogitechHidBattery.IsSupportedReceiverInstance("usb\\vid_046d&pid_c54d&rev_0100\\fixture"), "physical C54D instance identity recognized");
        Check(!LogitechHidBattery.IsSupportedReceiverInstance("USB\\VID_046D&PID_C54D0\\fixture")
            && !LogitechHidBattery.IsSupportedReceiverInstance("USB\\VID_046D&PID_C54D&MI_02\\fixture")
            && !LogitechHidBattery.IsSupportedReceiverInstance(null), "adjacent PID and logical interface cannot stand in for physical receiver");
        Check(LogitechHidBattery.SupportsPercentage(0x1004, [0, 2, 0]), "1004 explicit percentage capability accepted");
        Check(!LogitechHidBattery.SupportsPercentage(0x1004, [15, 0, 0]), "1004 coarse levels never treated as percent");
        Check(LogitechHidBattery.SupportsPercentage(0x1000, [10, 2, 0]), "1000 mileage capability accepted");
        Check(!LogitechHidBattery.SupportsPercentage(0x1000, [4, 2, 0]), "1000 four coarse levels never treated as percent");
        Check(!LogitechHidBattery.SupportsPercentage(0x1000, [10, 0, 0]), "1000 mileage flag required");
        Check(!LogitechHidBattery.SupportsPercentage(0x1004, []) && !LogitechHidBattery.SupportsPercentage(0x1001, [10, 2, 0]), "missing capabilities and voltage feature rejected");
        bool Decode(ushort feature, byte[] payload, bool supported, out int p, out bool? c) =>
            LogitechHidBattery.TryDecodeBattery(feature, feature == 0x1004 && payload.Length == 3 ? [..payload, 0] : payload, supported, out p, out c);
        Check(Decode(0x1004, [20, 0, 0], true, out var percent, out var charging) && percent == 20 && charging == false, "1004 device-reported 20 percent decoded");
        Check(Decode(0x1004, [0, 0, 0], true, out percent, out _) && percent == 0, "1004 exact real zero preserved");
        Check(Decode(0x1004, [100, 0, 3], true, out percent, out _) && percent == 100, "1004 full battery preserved");
        Check(Decode(0x1004, [20, 0, 1], true, out _, out charging) && charging == true, "1004 charging status decoded");
        Check(Decode(0x1004, [20, 0, 2], true, out _, out charging) && charging == true, "1004 slow charging status decoded");
        Check(!Decode(0x1004, [20, 0, 4], true, out _, out _), "1004 battery error cannot become an alert");
        Check(!Decode(0x1004, [101, 0, 0], true, out _, out _) && !Decode(0x1004, [255, 0, 0], true, out _, out _), "1004 unknown and invalid percentages rejected");
        Check(!Decode(0x1004, [20, 0, 0], false, out _, out _) && !Decode(0x1004, [20], true, out _, out _), "unsupported or truncated battery report rejected");
        Check(!LogitechHidBattery.TryDecodeBattery(0x1004, [20, 0, 0], true, out _, out _), "1004 fourth status parameter required");
        Check(Decode(0x1000, [20, 10, 0], true, out percent, out charging) && percent == 20 && charging == false, "1000 precise mileage decoded");
        Check(!Decode(0x1000, [0, 0, 0], true, out _, out _), "1000 ambiguous zero remains unknown");
        Check(!Decode(0x1000, [0, 0, 1], true, out _, out _), "1000 zero during charging remains unknown");
        Check(!Decode(0x1000, [0, 0, 3], true, out _, out _), "1000 ambiguous zero remains unknown even at full status");
        Check(!Decode(0x1000, [20, 0, 0], false, out _, out _), "1000 coarse-only device never exports a percent");
        Check(!Decode(0x1000, [12, 0, 7], true, out _, out _) && !Decode(0x1000, [12, 0, 8], true, out _, out _)
            && !Decode(0x1000, [12, 0, 255], true, out _, out _), "1000 charging error and reserved states cannot alert");
        Check(!Decode(0x1004, [12, 0, 5], true, out _, out _) && !Decode(0x1004, [12, 0, 255], true, out _, out _),
            "1004 reserved battery states cannot alert");
        byte[] reply = [0x11, 1, 6, 0x1c, 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Check(LogitechHidBattery.MatchesResponse(reply, 1, 6, 1, 0xc), "battery reply matches slot feature function and software ID");
        Check(LogitechHidBattery.MatchesResponse([0x10, 1, 0, 0x0c, 6, 0, 5], 1, 0, 0, 0xc), "short ROOT reply is accepted alongside long battery reply");
        Check(!LogitechHidBattery.MatchesResponse(reply, 2, 6, 1, 0xc), "another paired slot cannot supply this mouse battery");
        Check(!LogitechHidBattery.MatchesResponse(reply, 1, 7, 1, 0xc), "another feature response cannot supply battery");
        Check(!LogitechHidBattery.MatchesResponse(reply, 1, 6, 0, 0xc), "capabilities response cannot masquerade as status");
        Check(!LogitechHidBattery.MatchesResponse(reply, 1, 6, 1, 0xd), "G HUB response software ID is ignored");
        var eventReport = (byte[])reply.Clone(); eventReport[3] = 0x10;
        Check(!LogitechHidBattery.MatchesResponse(eventReport, 1, 6, 1, 0xc), "unsolicited event cannot masquerade as response");
        Check(!LogitechHidBattery.MatchesResponse([0x11, 1, 6], 1, 6, 1, 0xc), "truncated response rejected");
        byte[] protocolError = [0x10, 1, 0x8f, 6, 0x1c, 9, 0];
        Check(LogitechHidBattery.TryGetProtocolError(protocolError, 1, 6, 1, 0xc, out var error) && error == 9,
            "matching protocol error decoded without exporting battery");
        Check(!LogitechHidBattery.TryGetProtocolError(protocolError, 1, 6, 1, 0xd, out _), "other software protocol error ignored");
        Console.WriteLine($"{count} HID protocol checks passed.");
        return count;
    }
}
