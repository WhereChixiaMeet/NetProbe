using System;
using System.Collections.Generic;

namespace NetProbe
{
    // Перевод интерфейса. Строки в коде — русские литералы-ключи (внутренняя реализация, не выбираемый язык);
    // выбранный язык подменяет их из словаря Lang.En.cs / Lang.Es.cs / Lang.Zh.cs. Нет строки в словаре языка —
    // берётся английская. Русский в 3.0 из выбора убран: English · Español · 中文 (по умолчанию English).
    public static partial class Tr
    {
        public static readonly string[] Codes = { "en", "es", "zh" };
        public static readonly string[] Names = { "English", "Español", "中文" };

        public static bool Ru; // только для самопроверки/отладки, из интерфейса не включается
        static Dictionary<string, string> Active;
        public static string Code = "en";

        public static bool IsLang(string code) { return Array.IndexOf(Codes, code) >= 0; }
        public static int IndexOf(string code) { int i = Array.IndexOf(Codes, code); return i < 0 ? 0 : i; }

        public static void Set(string code)
        {
            Ru = false;
            Code = IsLang(code) ? code : "en";
            Active = Code == "es" ? Es : Code == "zh" ? Zh : null;
        }

        public static string Tx(string ru)
        {
            if (Ru || ru == null) return ru;
            string v;
            if (Active != null && Active.TryGetValue(ru, out v)) return v;
            return En.TryGetValue(ru, out v) ? v : ru;
        }

        // для --selftest: строки без пары в словаре и сами словари
        public static Dictionary<string, string> Dict(string code) { return code == "es" ? Es : code == "zh" ? Zh : En; }
    }
}
