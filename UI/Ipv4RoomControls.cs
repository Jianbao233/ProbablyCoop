using System;
using System.Net;

namespace ProbablyCoop;

internal sealed class Ipv4RoomControls
{
    private CustomUIElement hint, address, port, host, mapping, listening;

    private static string Loc(string key, params object[] args) => ModHelper.GetLocalized("probably_coop", key, args);

    internal string Address => address.GetString();
    internal string Port => port.GetString();
    internal string Mapping => mapping.GetString();
    internal bool InputFocused => Focused(address) || Focused(port) || Focused(mapping);

    private static bool Focused(CustomUIElement element) => element?.input != null && element.input.isFocused;

    internal void AddMapping(CustomUIBuilder builder, Action join)
    {
        builder.AddInput(Loc("mapped_address_hint")); mapping = builder.Last;
        mapping.input.characterLimit = 24;
        mapping.input.onSubmit.AddListener(_ => join());
    }

    internal void AddHost(CustomUIBuilder builder, Action create, bool compact = false)
    {
        if (!compact) builder.BeginRow(6);
        builder.AddInput(Loc("host_address_hint"), onTyping: value => host.SetInteractable(!string.IsNullOrWhiteSpace(value)));
        address = builder.Last;
        address.input.characterLimit = 15;
        address.input.onSubmit.AddListener(_ => create());
        if (compact) builder.BeginRow(6);
        builder.AddInput(Loc("udp_port_hint")); port = builder.Last;
        port.input.characterLimit = 5;
        port.input.onSubmit.AddListener(_ => create());
        builder.AddButton(Loc("lan_create"), create); host = builder.Last;
        if (compact) builder.WithTooltip(Loc("ipv4_mapping_hint"));
        builder.End();
        if (!compact)
        {
            builder.AddLabel(Loc("ipv4_mapping_hint")); hint = builder.Last;
            AddListening(builder);
        }
    }

    internal void AddListening(CustomUIBuilder builder)
    {
        builder.AddLabel(""); listening = builder.Last;
    }

    internal void Refresh(bool showHost, bool showMapping, IPEndPoint endpoint)
    {
        hint?.SetVisible(showHost);
        address.SetVisible(showHost);
        port.SetVisible(showHost);
        host.SetVisible(showHost);
        host.SetInteractable(!string.IsNullOrWhiteSpace(Address));
        mapping.SetVisible(showMapping);
        listening.SetVisible(endpoint != null);
        if (endpoint != null) listening.SetText(Loc("udp_listening", endpoint));
    }

    internal static bool IsIpv4Code(string value) => value?.Trim().StartsWith("PSI-", StringComparison.Ordinal) == true;
}
