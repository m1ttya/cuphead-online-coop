using System;

namespace CupheadOnline.Sync
{
    public static class AssetNames
    {
        public static string Sprite(string name)
        {
            // SpriteAtlas.GetSprite returns a clone, not an atlas entry with a new lookup name.
            while (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 7).TrimEnd();
            return name;
        }
    }
}
