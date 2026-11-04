using System;
using System.Collections.Generic;

// The preview host is a toolkit for the previewed mod: the mirror generator emits the toolkit side of the bridge
// here, exactly as for Acrux UI, so the mod's own client code runs against it unchanged.
[assembly: AcruxPreview.AcruxUiToolkitExport]

namespace AcruxPreview
{
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class AcruxUiToolkitExportAttribute : Attribute
    {
    }

    internal static class ToolkitApi
    {
        const int ProtocolVersion = 1;
        static Dictionary<string, Delegate> _api;

        public static Dictionary<string, Delegate> Api
        {
            get
            {
                if (_api == null)
                {
                    var api = new Dictionary<string, Delegate>(StringComparer.Ordinal);
                    Acrux.Toolkit.Server.ServerUi.Register(api);
                    Acrux.Toolkit.Server.AcruxUiToolkitBridge.RegisterGenerated(api);
                    api["Toolkit.ProtocolVersion"] = new Func<int>(() => ProtocolVersion);
                    _api = api;
                }
                return _api;
            }
        }
    }
}
