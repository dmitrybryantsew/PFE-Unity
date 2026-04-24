using System;
using System.IO;
using System.Linq;

namespace PFE.Editor.Importers
{
    public static class SourceImportPaths
    {
        public const string ImportRootEnvironmentVariable = "PFE_IMPORT_ROOT";

        static string ImportRoot => Normalize(Environment.GetEnvironmentVariable(ImportRootEnvironmentVariable));

        public static string AllDataAsPath => Combine("pfe", "scripts", "fe", "AllData.as");
        public static string FeScriptsRoot => Combine("pfe", "scripts", "fe");
        public static string ScriptsRoot => Combine("pfe", "scripts");
        public static string PfeRoot => Combine("pfe");
        public static string PfeSpritesRoot => Combine("pfe", "sprites");
        public static string AssetsSwfPath => Combine("pfe", "scripts", "_assets", "assets.swf");
        public static string AssetsExportRoot => Combine("pfe", "scripts", "_assets");
        public static string SoundDefinitionPath => Combine("pfe", "scripts", "fe", "Snd.as");
        public static string SourceProjectRoot => ImportRoot;
        public static string PfeSwfPath => Combine("pfe.swf");

        public static string[] RoomGraphicsExportRoots => NonEmpty(
            PfeSpritesRoot,
            TextureSpritesRoot("texture"),
            TextureSpritesRoot("texture1"));

        public static string[] MapObjectExportRoots => NonEmpty(
            TextureSpritesRoot("texture1"),
            PfeSpritesRoot);

        public static string[] MapObjectSymbolInventoryPaths => NonEmpty(
            SymbolInventoryPath("texture1"));

        public static string[] MapObjectWrapperScriptRoots => NonEmpty(
            TextureScriptsRoot("texture1"),
            ScriptsRoot);

        public static string TextureImagesRoot(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "images");
        }

        public static string TextureSpritesRoot(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "sprites");
        }

        public static string TextureScriptsRoot(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "scripts");
        }

        public static string SymbolInventoryPath(string swfFolderName)
        {
            return Combine($"{swfFolderName}.swf", "symbolClass", "symbols.csv");
        }

        static string Combine(params string[] segments)
        {
            if (string.IsNullOrWhiteSpace(ImportRoot))
                return string.Empty;

            var parts = new[] { ImportRoot }.Concat(segments).ToArray();
            return Normalize(Path.Combine(parts));
        }

        static string[] NonEmpty(params string[] values)
        {
            return values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }

        static string Normalize(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Replace('\\', '/');
        }
    }
}
