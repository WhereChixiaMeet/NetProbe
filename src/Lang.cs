namespace NetProbe
{
    // Перевод интерфейса. Строки в коде — русские; при английском языке (по умолчанию) они подменяются из словаря Lang.En.cs.
    public static partial class Tr
    {
        public static bool Ru;
        public static string Tx(string ru)
        {
            if (Ru || ru == null) return ru;
            string en;
            return En.TryGetValue(ru, out en) ? en : ru;
        }
    }
}
