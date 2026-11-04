using Acrux.Styling;

namespace Acrux.Markup
{
    /// <summary>
    /// Runtime XAML metadata for every control compiled with Acrux. The Acrux source
    /// generator implements these methods, so no reflection is needed at runtime.
    /// </summary>
    internal static partial class GeneratedTypeCatalog
    {
        internal static void Register(TypeRegistry registry)
        {
            RegisterTemplateTypes(registry);
        }

        internal static void Register(StylePropertyRegistry registry)
        {
            RegisterStyleTypes(registry);
        }

        static volatile bool _styledPropertiesEnsured;
        static readonly object EnsureLock = new object();

        internal static void EnsureStyledProperties()
        {
            if (_styledPropertiesEnsured)
                return;
            lock (EnsureLock)
            {
                if (_styledPropertiesEnsured)
                    return;
                TouchStyledProperties();
                _styledPropertiesEnsured = true;
            }
        }

        static void Touch(object property)
        {
        }

        static partial void TouchStyledProperties();

        static partial void RegisterTemplateTypes(TypeRegistry registry);

        static partial void RegisterStyleTypes(StylePropertyRegistry registry);
    }
}
