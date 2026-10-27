using System.Collections.Generic;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// A layer's fields as seen through the instance it is drawn in: the main component's node
    /// change, overlaid by the override and derived-layout rows that instances write for it
    /// (<c>symbolData.symbolOverrides</c>, <c>derivedSymbolData</c>). The last row that sets a field
    /// wins; outside instances it is just the node change.
    ///
    /// Mirrors figcraft's <c>View::with</c> (<c>crates/format/src/fig/map/layer.rs</c>).
    /// </summary>
    public sealed class FigView
    {
        public readonly KiwiValue Base;

        /// <summary>Rows in application order (later wins). Null when there are none.</summary>
        public readonly List<KiwiValue> Rows;

        /// <summary>Rows whose size / transform no longer hold (a nested instance resized since).</summary>
        public readonly HashSet<KiwiValue> StaleRows;

        public FigView(KiwiValue baseValue, List<KiwiValue> rows = null, HashSet<KiwiValue> staleRows = null)
        {
            Base = baseValue;
            Rows = rows != null && rows.Count > 0 ? rows : null;
            StaleRows = staleRows;
        }

        public bool HasRows => Rows != null;

        public KiwiValue Get(string field)
        {
            if (Rows != null)
            {
                for (int i = Rows.Count - 1; i >= 0; i--)
                {
                    KiwiValue row = Rows[i];
                    if (StaleRows != null && StaleRows.Contains(row) && (field == "size" || field == "transform"))
                    {
                        continue;
                    }

                    KiwiValue v = row.Get(field);
                    if (v != null)
                    {
                        return v;
                    }
                }
            }

            return Base.Get(field);
        }

        /// <summary>Whether an override or derived row (not the base) sets <paramref name="field"/>.</summary>
        public bool Overrides(string field)
        {
            if (Rows == null)
            {
                return false;
            }

            foreach (KiwiValue row in Rows)
            {
                if (row.Get(field) != null && !(StaleRows != null && StaleRows.Contains(row) && (field == "size" || field == "transform")))
                {
                    return true;
                }
            }

            return false;
        }

        public KiwiValue Path(string path)
        {
            int dot = path.IndexOf('.');
            if (dot < 0)
            {
                return Get(path);
            }

            return Get(path.Substring(0, dot))?.Path(path.Substring(dot + 1));
        }

        public string Str(string field) => Get(field)?.String;

        public string EnumName(string field) => Get(field)?.EnumName;

        public float Float(string field, float fallback) => Get(field)?.AsFloat ?? fallback;

        public bool Bool(string field, bool fallback) => Get(field)?.AsBool ?? fallback;

        public List<KiwiValue> List(string field) => Get(field)?.Items;
    }
}
