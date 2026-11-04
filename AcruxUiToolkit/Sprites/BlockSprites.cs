using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using VRage.Game;
using VRage.ObjectBuilders;

namespace Acrux.Sprites
{
    /// <summary>
    /// Block icons as LCD sprites. Item icons are LCD sprites from the start; a block's icon is not, until an LCD
    /// texture definition names it. Registering a block adds one, named after the block's definition id
    /// ("MyObjectBuilder_Thrust/LargeBlockLargeThrust"), so every client of the toolkit shares the same names.
    /// Game thread only. Registrations last for the session.
    /// </summary>
    public static class BlockSprites
    {
        const string OBJECT_BUILDER_PREFIX = "MyObjectBuilder_";
        const string GROUP_SPRITE = "Acrux_TerminalGroup";
        // MyGuiConstants.TEXTURE_TERMINAL_GROUP: the icon the game gives a block group (toolbar, terminal).
        const string GROUP_TEXTURE = "Textures\\GUI\\Icons\\GroupIcon.dds";
        static bool _groupRegistered;

        static readonly HashSet<MyCubeBlockDefinition> Registered = new HashSet<MyCubeBlockDefinition>();
        static Dictionary<string, MyCubeBlockDefinition> _byId;

        /// <summary>
        /// Registers the icon of the block named by <paramref name="blockDefinitionId"/> and returns its sprite
        /// name, or null when no block has that id. The id is "Type/Subtype"; the type may leave out its
        /// "MyObjectBuilder_" prefix.
        /// </summary>
        public static string Register(string blockDefinitionId)
        {
            MyCubeBlockDefinition definition = Find(blockDefinitionId);
            return definition == null ? null : Register(definition);
        }

        /// <summary>
        /// The game's block-group icon (several blocks at once), registered as an LCD sprite on first use; returns
        /// its sprite name.
        /// </summary>
        public static string RegisterGroup()
        {
            if (_groupRegistered || MyDefinitionManager.Static == null)
                return GROUP_SPRITE;
            var texture = new MyLCDTextureDefinition
            {
                Id = new MyDefinitionId((MyObjectBuilderType)typeof(MyObjectBuilder_LCDTextureDefinition), GROUP_SPRITE),
                Public = false,
                LocalizationId = GROUP_SPRITE,
                SpritePath = GROUP_TEXTURE,
                Selectable = false
            };
            MyDefinitionManager.Static.Definitions.AddOrReplaceDefinition(texture);
            _groupRegistered = true;
            return GROUP_SPRITE;
        }

        /// <summary>True when the sprite is a block icon this session registered.</summary>
        public static bool IsRegistered(string spriteName)
        {
            MyCubeBlockDefinition definition = Find(spriteName);
            return definition != null && Registered.Contains(definition) &&
                   string.Equals(definition.Id.ToString(), spriteName, StringComparison.Ordinal);
        }

        /// <summary>Registers every block's icon; returns how many were new.</summary>
        public static int RegisterAll()
        {
            Index();
            int added = 0;
            foreach (MyCubeBlockDefinition definition in _byId.Values)
            {
                if (Registered.Contains(definition))
                    continue;
                Register(definition);
                added++;
            }
            return added;
        }

        /// <summary>
        /// Registers the registered icons again, for when a mod (Colorful Icons) changed the blocks' icon paths.
        /// </summary>
        public static void Refresh()
        {
            if (Registered.Count == 0)
                return;
            var definitions = new List<MyCubeBlockDefinition>(Registered);
            Registered.Clear();
            for (int i = 0; i < definitions.Count; i++)
            {
                if (definitions[i] != null)
                    Register(definitions[i]);
            }
        }

        /// <summary>Adds the sprite names of the registered block icons.</summary>
        public static void GetRegistered(List<string> spriteNames)
        {
            if (spriteNames == null)
                return;
            foreach (MyCubeBlockDefinition definition in Registered)
            {
                if (definition != null)
                    spriteNames.Add(definition.Id.ToString());
            }
        }

        internal static void ReleaseShared()
        {
            Registered.Clear();
            _byId = null;
            _groupRegistered = false;
        }

        static string Register(MyCubeBlockDefinition definition)
        {
            string name = definition.Id.ToString();
            if (!Registered.Add(definition) || MyDefinitionManager.Static == null)
                return name;
            var texture = new MyLCDTextureDefinition
            {
                Id = new MyDefinitionId((MyObjectBuilderType)typeof(MyObjectBuilder_LCDTextureDefinition), name),
                Public = false,
                LocalizationId = definition.DisplayNameString,
                SpritePath = definition.Icons != null && definition.Icons.Length != 0 ? definition.Icons[0] : string.Empty,
                Selectable = false
            };
            MyDefinitionManager.Static.Definitions.AddOrReplaceDefinition(texture);
            return name;
        }

        static MyCubeBlockDefinition Find(string blockDefinitionId)
        {
            if (string.IsNullOrEmpty(blockDefinitionId) || MyDefinitionManager.Static == null)
                return null;
            Index();
            MyCubeBlockDefinition definition;
            if (_byId.TryGetValue(blockDefinitionId, out definition))
                return definition;
            if (!blockDefinitionId.StartsWith(OBJECT_BUILDER_PREFIX, StringComparison.Ordinal) &&
                _byId.TryGetValue(OBJECT_BUILDER_PREFIX + blockDefinitionId, out definition))
                return definition;
            return null;
        }

        // Definitions do not change within a session, so the lookup is built once instead of scanning every
        // definition per name.
        static void Index()
        {
            if (_byId != null)
                return;
            _byId = new Dictionary<string, MyCubeBlockDefinition>(StringComparer.Ordinal);
            if (MyDefinitionManager.Static == null)
                return;
            foreach (MyDefinitionBase definitionBase in MyDefinitionManager.Static.GetAllDefinitions())
            {
                var definition = definitionBase as MyCubeBlockDefinition;
                if (definition != null)
                    _byId[definition.Id.ToString()] = definition;
            }
        }
    }
}
