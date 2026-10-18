namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The clipboard's privacy rules as the real wrapper decides them (review F5): which markers make content private, and
/// which formats count as more than plain text. <see cref="FakeClipboardWrapper"/> takes the markers as given, so these
/// are the only tests of the names and the fail-closed rule: a typo in a format name would register a format no copy
/// carries and switch the password-manager protection off. The decisions are pure; the "clipboard" here is a dictionary
/// keyed by the format names written out in full, never the user's clipboard.
/// </summary>
public class NativeClipboardWrapperTests
{
    // The names as Windows and password managers register them, written out here on purpose
    private const string ExcludeFromMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";
    private const string ViewerIgnore = "Clipboard Viewer Ignore";
    private const string ProtectionLabel = "EnterpriseDataProtectionId";

    private const uint CfText = 1, CfOemText = 7, CfUnicodeText = 13, CfHdrop = 15, CfLocale = 16, CfDib = 8;

    /// <summary>The markers of a clipboard holding <paramref name="formats"/> (name: its DWORD, null when unreadable).</summary>
    private static ClipboardPrivacyMarkers Markers(Dictionary<string, uint?> formats, params string[] unregistered)
    {
        var dwordReads = new List<string>();
        var markers = NativeClipboardWrapper.PrivacyMarkers(
            name => unregistered.Contains(name) ? NativeClipboardWrapper.FormatPresence.Unregistered
                : formats.ContainsKey(name) ? NativeClipboardWrapper.FormatPresence.Present
                : NativeClipboardWrapper.FormatPresence.Absent,
            name =>
            {
                dwordReads.Add(name);
                return formats[name];
            });
        dwordReads.Where(name => !formats.ContainsKey(name)).Should().BeEmpty("only a DWORD that is on the clipboard is read");
        return markers;
    }

    [Fact]
    public void TheFormatNames_AreThoseWindowsAndPasswordManagersRegister()
    {
        NativeClipboardWrapper.ExcludeFromMonitorFormat.Should().Be(ExcludeFromMonitor);
        NativeClipboardWrapper.HistoryFormat.Should().Be(CanIncludeInHistory);
        NativeClipboardWrapper.CloudFormat.Should().Be(CanUploadToCloud);
        NativeClipboardWrapper.ViewerIgnoreFormat.Should().Be(ViewerIgnore);
        NativeClipboardWrapper.EnterpriseDataProtectionFormat.Should().Be(ProtectionLabel);
        NativeClipboardWrapper.BookkeepingFormats.Should().BeEquivalentTo(
            ExcludeFromMonitor, CanIncludeInHistory, CanUploadToCloud, ViewerIgnore, "DataObject", "Ole Private Data", "OleClipboardPersistOnFlush");
    }

    [Fact]
    public void AnOrdinaryCopy_HasNoMarker()
    {
        Markers([]).Should().Be(ClipboardPrivacyMarkers.None);
    }

    [Fact]
    public void ExcludeClipboardContentFromMonitorProcessing_MakesItPrivate()
    {
        Markers(new() { [ExcludeFromMonitor] = null }).Should().Be(ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing);
    }

    [Fact]
    public void ClipboardViewerIgnore_MakesItPrivate()
    {
        Markers(new() { [ViewerIgnore] = null }).Should().Be(ClipboardPrivacyMarkers.ClipboardViewerIgnore);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(0xFFFFFFFFu, false)]
    public void CanIncludeInClipboardHistory_MakesItPrivate_OnlyWhenZero(uint value, bool isPrivate)
    {
        Markers(new() { [CanIncludeInHistory] = value }).Should().Be(
            isPrivate ? ClipboardPrivacyMarkers.NotForClipboardHistory : ClipboardPrivacyMarkers.None);
    }

    [Fact]
    public void CanIncludeInClipboardHistory_WhoseValueCannotBeRead_MakesItPrivate()
    {
        Markers(new() { [CanIncludeInHistory] = null }).Should().Be(ClipboardPrivacyMarkers.NotForClipboardHistory);
    }

    [Fact]
    public void CanUploadToCloudClipboard_IsNoPrivacyMarker()
    {
        // A copy kept off the cloud clipboard (0) is still the user's ordinary copy
        Markers(new() { [CanUploadToCloud] = 0 }).Should().Be(ClipboardPrivacyMarkers.None);
    }

    [Theory]
    [InlineData(ExcludeFromMonitor, ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing)]
    [InlineData(ViewerIgnore, ClipboardPrivacyMarkers.ClipboardViewerIgnore)]
    [InlineData(CanIncludeInHistory, ClipboardPrivacyMarkers.NotForClipboardHistory)]
    public void AMarkerWhoseNameCannotBeRegistered_CountsAsSet(string name, ClipboardPrivacyMarkers marker)
    {
        // Whether it is there is not known: fails closed
        Markers([], unregistered: name).Should().Be(marker);
    }

    [Fact]
    public void WhatSetClipboardWrites_IsPrivate_ByEachOfItsMarkers()
    {
        // set_clipboard puts the three DWORDs at 0 (WriteOpen): two of them are markers
        Markers(new() { [ExcludeFromMonitor] = 0, [CanIncludeInHistory] = 0, [CanUploadToCloud] = 0 }).Should().Be(
            ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing | ClipboardPrivacyMarkers.NotForClipboardHistory);
    }

    // ---- Formats besides plain text ----

    /// <summary>The IDs RegisterClipboardFormat gives the names it knows; 0 for any other name.</summary>
    private static readonly Dictionary<string, uint> Registered = new()
    {
        [ExcludeFromMonitor] = 0xC001,
        [CanIncludeInHistory] = 0xC002,
        [CanUploadToCloud] = 0xC003,
        [ViewerIgnore] = 0xC004,
        ["DataObject"] = 0xC005,
        ["Ole Private Data"] = 0xC006,
        ["OleClipboardPersistOnFlush"] = 0xC007,
        [ProtectionLabel] = 0xC008,
        ["HTML Format"] = 0xC009
    };

    private static bool HasOtherFormats(IEnumerable<uint> formats, Dictionary<string, uint>? registered = null, bool emptyLabel = true) =>
        NativeClipboardWrapper.HasOtherFormats(formats, name => (registered ?? Registered).GetValueOrDefault(name),
            format => format == Registered[ProtectionLabel] ? emptyLabel : throw new InvalidOperationException("only the label's data is read"));

    [Fact]
    public void PlainText_AndTheFormatsWindowsMakesFromIt_AreNotOtherFormats()
    {
        HasOtherFormats([CfUnicodeText, CfText, CfOemText, CfLocale]).Should().BeFalse();
    }

    [Fact]
    public void TheMarkers_OlesBookkeeping_AndAnEmptyProtectionLabel_AreNotOtherFormats()
    {
        HasOtherFormats([CfUnicodeText, .. Registered.Where(r => r.Key != "HTML Format").Select(r => r.Value)]).Should().BeFalse();
    }

    [Theory]
    [InlineData(CfHdrop)]
    [InlineData(CfDib)]
    [InlineData(0xC009u)] // HTML Format
    [InlineData(0xC0FFu)] // an application's own format
    public void FilesImagesHtmlAndAppFormats_AreOtherFormats(uint format)
    {
        HasOtherFormats([CfUnicodeText, format]).Should().BeTrue();
    }

    [Fact]
    public void AProtectionLabelThatNamesAnIdentity_IsAnotherFormat()
    {
        // Pasting the text alone would strip the enterprise protection
        HasOtherFormats([CfUnicodeText, Registered[ProtectionLabel]], emptyLabel: false).Should().BeTrue();
    }

    [Fact]
    public void ABookkeepingNameThatCannotBeRegistered_IsNotRecognised_SoItsFormatCounts()
    {
        // Fails closed: the format on the clipboard cannot be told from an application's own
        var withoutDataObject = Registered.Where(r => r.Key != "DataObject").ToDictionary(r => r.Key, r => r.Value);

        HasOtherFormats([CfUnicodeText, Registered["DataObject"]], withoutDataObject).Should().BeTrue();
    }

    [Fact]
    public void TheFirstFormatThatCounts_EndsTheLook()
    {
        // EnumClipboardFormats is read one format at a time: nothing after the first that counts is asked for
        static IEnumerable<uint> Formats()
        {
            yield return CfUnicodeText;
            yield return CfHdrop;
            throw new InvalidOperationException("read past the first format that counts");
        }

        HasOtherFormats(Formats()).Should().BeTrue();
    }
}
