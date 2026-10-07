using System;
using System.Reflection;
using UnityEngine;

namespace AliceInCradleLink
{
    internal sealed class VitalReader
    {
        private readonly FieldInfo _hp;
        private readonly FieldInfo _maxHp;
        private readonly FieldInfo _mp;
        private readonly FieldInfo _maxMp;
        private readonly FieldInfo _ep;

        public VitalReader(Type type)
        {
            _hp = Find(type, "hp");
            _maxHp = Find(type, "maxhp");
            _mp = Find(type, "mp");
            _maxMp = Find(type, "maxmp");
            _ep = Find(type, "ep");
        }

        public bool Ready => _hp != null && _maxHp != null && _mp != null &&
                             _maxMp != null && _ep != null;

        public void Read(Component target, out int hp, out int hpMax,
                         out int mp, out int mpMax, out int ep)
        {
            hp = Int(_hp, target);
            hpMax = Int(_maxHp, target);
            mp = Int(_mp, target);
            mpMax = Int(_maxMp, target);
            ep = Int(_ep, target);
        }

        private static int Int(FieldInfo field, object target)
        {
            if (field == null || target == null) return 0;
            try
            {
                return Convert.ToInt32(field.GetValue(target));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static FieldInfo Find(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, BindingFlags.Instance |
                                BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }
    }
}
