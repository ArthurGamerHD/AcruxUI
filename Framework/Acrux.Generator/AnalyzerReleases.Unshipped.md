; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules
Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ACRUX001 | Acrux.Generator | Error | Acrux view markup must be valid
ACRUX002 | Acrux.Generator | Error | Acrux view types must resolve
ACRUX003 | Acrux.Generator | Error | Acrux view properties must resolve
ACRUX004 | Acrux.Generator | Error | Compiled bindings must be valid
ACRUX005 | Acrux.Generator | Error | Acrux markup must use supported constructs
ACRUX006 | Acrux.Generator | Warning | StyleInclude sources must resolve to included XAML assets
ACRUX007 | Acrux.Generator | Warning | XAML members must be whitelisted base members
ACRUX008 | Acrux.Generator | Warning | XAML elements must be whitelisted base members
ACRUX009 | Acrux.Generator | Warning | Styled-property enums clients cannot name must declare an explicit underlying type
ACRUX010 | Acrux.Generator | Error | x:DataType view models must be observable (INotifyPropertyChanged)
ACRUX011 | Acrux.Generator | Error | [PreviewWith] values must fit their member
