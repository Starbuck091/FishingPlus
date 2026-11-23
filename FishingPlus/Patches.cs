using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.Gameplay;
using Il2CppTLD.Gameplay.Fishing;
using Il2CppTLD.IntBackedUnit;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace FishingPlus
{
    /// <summary>
    /// Настройки жерлиц и златоглазки-наживки.
    /// Меняйте цифры — код не трогайте.
    /// </summary>
    internal static class TipUpConfig
    {
        public const float MIN_MINUTES = 240f;    // 4 часа
        public const float MAX_MINUTES = 480f;    // 8 часов
        public const float SIZE_MULT = 2.0f;      // +100% к размеру
        public const float CAPACITY_MULT = 2.5f;  // +150% к ёмкости
        public const float GOLDEYE_SIZE_MULT = 1.5f; // +50% к размеру от златоглазки
        public const float AURORA_MULT = 0.40f;
        public const float ELECTRO_FOG_MULT = 0.45f;
    }

    // 1. Выбор наживки
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.SetSelectedBait))]
    internal static class LogBait
    {
        private static void Postfix(GearItem baitGearItem)
        {
            if (baitGearItem == null) { FishingPlusLogger.Msg("Наживка снята"); return; }
            BaitItem bait = baitGearItem.GetComponent<BaitItem>();
            FishingPlusLogger.Msg("Наживка: " + baitGearItem.name
                + " | BaitItem: " + (bait != null)
                + (bait != null ? " | MinFishWeightModifier = " + bait.m_MinFishWeightModifier : ""));
        }
    }

    // 2. Выбор приманки
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.SetSelectedLure))]
    internal static class LogLure
    {
        private static void Postfix(GearItem lureGearItem)
        {
            if (lureGearItem == null) { FishingPlusLogger.Msg("Приманка снята"); return; }
            LureItem lure = lureGearItem.GetComponent<LureItem>();
            FishingPlusLogger.Msg("Приманка: " + lureGearItem.name
                + " | LureItem: " + (lure != null)
                + (lure != null ? " | CatchModifier = " + lure.m_CatchModifier
                                  + " | RareFishModifier = " + lure.m_RareFishModifier : ""));
        }
    }

    // 3. Что лежит в списках окна рыбалки + детектор региона
    [HarmonyPatch(typeof(Panel_IceFishing), "RefreshRows")]
    internal static class LogRows
    {
        private static void Postfix(Panel_IceFishing __instance)
        {
            try { FishBaitRules.GetCurrentRegionName(); } catch { }

            Dump("ПРИМАНКИ", __instance.m_LureSelection);
            Dump("НАЖИВКИ", __instance.m_BaitSelection);
        }

        private static void Dump(string title, IceFishingSelectionRow row)
        {
            if (row == null || row.m_Items == null) { FishingPlusLogger.Msg(title + ": пусто"); return; }
            FishingPlusLogger.Msg(title + ": предметов " + row.m_Items.Count);
            for (int i = 0; i < row.m_Items.Count; i++)
            {
                GearItem g = row.m_Items[i];
                if (g == null) continue;
                FishingPlusLogger.Msg("   " + g.name
                    + " | BaitItem: " + (g.GetComponent<BaitItem>() != null)
                    + " | LureItem: " + (g.GetComponent<LureItem>() != null));
            }
        }
    }

    // 4. Можно ли рыбачить сейчас
    [HarmonyPatch(typeof(Panel_IceFishing), nameof(Panel_IceFishing.CanDoFishing))]
    internal static class LogCanDo
    {
        private static string last = "";

        private static void Postfix(Panel_IceFishing __instance, bool __result)
        {
            string bait = GetSelectedItemName(__instance.m_BaitSelection);
            string lure = GetSelectedItemName(__instance.m_LureSelection);

            string now = "CanDoFishing = " + __result + " | наживка: " + bait + " | приманка: " + lure;
            if (now == last) return;
            last = now;
            FishingPlusLogger.Msg(now);
        }

        private static string GetSelectedItemName(IceFishingSelectionRow row)
        {
            if (row == null) return "нет";
            try
            {
                GearItem item = row.SelectedItem;
                return item != null ? item.name : "нет";
            }
            catch (ArgumentOutOfRangeException) { return "нет"; }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("LogCanDo: " + e.GetType().Name);
                return "нет";
            }
        }
    }

    // 5. Учёт улова и истощение лунок (in-memory)
    internal static class HoleTracker
    {
        public class HoleState
        {
            public int CapacityFish;
            public int CaughtFish;
            public bool IsDepleted;
            public float DepletedAtGameHours;
            public float RestHours;
            public float LastDepletionLogGameHours = -9999f;
            public bool HasHadTipup;

            public bool IsResting => IsDepleted && GetCurrentGameHours() < DepletedAtGameHours + RestHours;
            public float HoursRemaining => UnityEngine.Mathf.Max(0f, DepletedAtGameHours + RestHours - GetCurrentGameHours());

            public void Reset()
            {
                CaughtFish = 0;
                IsDepleted = false;
                DepletedAtGameHours = 0f;
                CapacityFish = RollCapacityFish();
                RestHours = UnityEngine.Random.Range(24f, 168f);
                LastDepletionLogGameHours = -9999f;
                HasHadTipup = false;
            }
        }

        private static readonly Dictionary<string, HoleState> _states = new();

        public static void ResetAll()
        {
            _states.Clear();
            FishingPlusLogger.Msg("[FishingPlus] Все лунки сброшены.");
        }

        public static HoleState GetOrCreate(IceFishingHole hole)
        {
            string key = GetKey(hole);
            if (!_states.TryGetValue(key, out var state))
            {
                state = new HoleState();
                state.Reset();
                _states[key] = state;
                FishingPlusLogger.Msg("[FishingPlus] Новая лунка " + key
                    + ": ёмкость " + state.CapacityFish + " рыб"
                    + ", отдых " + (state.RestHours / 24f).ToString("F1") + " дн.");
            }
            else if (state.IsDepleted && !state.IsResting)
            {
                state.Reset();
                FishingPlusLogger.Msg("[FishingPlus] Лунка " + key
                    + " восстановилась: ёмкость " + state.CapacityFish + " рыб"
                    + ", отдых " + (state.RestHours / 24f).ToString("F1") + " дн.");
            }
            return state;
        }

        public static void AddCatch(IceFishingHole hole)
        {
            var state = GetOrCreate(hole);
            bool hasTipup = hole.m_ActiveTipup != null;

            // Жерлица обходит истощение — учитываем улов даже в resting-состоянии
            if (state.IsResting && !hasTipup)
            {
                FishingPlusLogger.Msg("[FishingPlus] Улов в истощённой лунке — не учитывается.");
                return;
            }

            state.CaughtFish++;
            FishingPlusLogger.Msg("[FishingPlus] Улов: " + state.CaughtFish + "/" + state.CapacityFish + " рыб");

            if (!state.IsDepleted && state.CaughtFish >= state.CapacityFish)
            {
                state.IsDepleted = true;
                state.DepletedAtGameHours = GetCurrentGameHours();
                FishingPlusLogger.Warning("[FishingPlus] !!! ЛУНКА ИСТОЩЕНА !!! Поймано "
                    + state.CaughtFish + "/" + state.CapacityFish + " рыб. Отдых "
                    + (state.RestHours / 24f).ToString("F1") + " дн.");
            }
        }

        public static bool TryLogDepletion(IceFishingHole hole)
        {
            var state = GetOrCreate(hole);
            if (!state.IsResting) return false;

            float now = GetCurrentGameHours();
            if (now - state.LastDepletionLogGameHours < 1f) return true;

            state.LastDepletionLogGameHours = now;
            FishingPlusLogger.Warning("[FishingPlus] ИСТОЩЁННАЯ ЛУНКА ("
                + state.CaughtFish + "/" + state.CapacityFish + " рыб). Отдых ещё "
                + state.HoursRemaining.ToString("F0") + " ч.");
            return true;
        }

        private static string GetKey(IceFishingHole hole)
        {
            try
            {
                var pos = hole.transform.position;
                return UnityEngine.Mathf.RoundToInt(pos.x) + "_" + UnityEngine.Mathf.RoundToInt(pos.z);
            }
            catch { return "unknown_" + hole.GetInstanceID(); }
        }

        public static float GetCurrentGameHours()
        {
            try
            {
                var tod = GameManager.GetTimeOfDayComponent();
                if (tod == null) return 0f;
                return tod.GetDayNumber() * 24f + tod.GetHour();
            }
            catch { return 0f; }
        }

        private static int RollCapacityFish()
        {
            float weatherFactor = 1.0f;
            try
            {
                var weather = GameManager.GetWeatherComponent();
                if (weather != null)
                {
                    if (weather.IsClear()) weatherFactor = 1.0f;
                    else if (weather.IsBlizzard()) weatherFactor = 0.25f;
                    else if (weather.IsHeavySnow()) weatherFactor = 0.4f;
                    else if (weather.IsLightSnow()) weatherFactor = 0.7f;
                    else if (weather.IsDenseFog() || weather.IsFoggy()) weatherFactor = 0.85f;
                }
                var aurora = GameManager.GetAuroraManager();
                if (aurora != null && aurora.AuroraIsActive()) weatherFactor = 1.5f;
            }
            catch { }

            int baseRoll = UnityEngine.Random.Range(1, 79);
            int result = UnityEngine.Mathf.RoundToInt(baseRoll * weatherFactor);
            return UnityEngine.Mathf.Clamp(result, 1, 78);
        }
    }

    // 6. Логирование истощения при попытке начать рыбалку
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.StartFishing))]
    internal static class LogStartFishing
    {
        private static void Postfix(IceFishingHole __instance)
        {
            HoleTracker.TryLogDepletion(__instance);
        }
    }

    // 7. Основные механики: удочка + синергия + погода + истощение + жерлица
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.RollNextCatch))]
    internal static class FishingTweaks
    {
        private const float NEVER_CATCH_MINUTES = 1000000f;
        private const float LURE_TIME_MULTIPLIER = 0.85f;
        private const float SYNERGY_TIME_MULTIPLIER = 0.75f;
        private const float SYNERGY_WEIGHT_MULTIPLIER = 1.25f;
        private const float SYNERGY_MIN_WEIGHT_MULTIPLIER = 1.15f;
        private const float MIN_TIME_MINUTES = 1.0f;
        private const float DEPLETED_MIN_MINUTES = 360f;
        private const float DEPLETED_MAX_MINUTES = 660f;
        private const float DEPLETED_WEIGHT_MULTIPLIER = 0.5f;

        private static Weather _weather;
        private static Wind _wind;
        private static AuroraManager _aurora;
        private static TimeOfDay _timeOfDay;

        private static void Postfix(IceFishingHole __instance)
        {
            CatchParameters c = __instance.m_NextCatch;
            if (c == null) return;

            bool hasBait = __instance.m_SelectedBait != null;
            bool hasLure = __instance.m_SelectedLure != null;
            bool hasTipup = __instance.m_ActiveTipup != null;

            // Нет снастей вообще — никогда
            if (!hasBait && !hasLure)
            {
                c.m_MinutesToNextCatch = NEVER_CATCH_MINUTES;
                FishingPlusLogger.Msg("[FishingPlus] Нет снастей — поклёвка отменена.");
                return;
            }

            // ============================================================
            // ЖЕРЛИЦА
            // ============================================================
            if (hasTipup)
            {
                // Жерлицы не используют приманку (lure) — только наживку.
                if (hasLure)
                {
                    c.m_MinutesToNextCatch = NEVER_CATCH_MINUTES;
                    FishingPlusLogger.Msg("[FishingPlus] Жерлица + приманка — несовместимо, поклёвки не будет.");
                    return;
                }

                float fixedTime = UnityEngine.Random.Range(TipUpConfig.MIN_MINUTES, TipUpConfig.MAX_MINUTES);
                string tipupReason;                                   // ← переименовали
                float tipupWeather = GetTipUpWeatherMultiplier(out tipupReason);
                c.m_MinutesToNextCatch = fixedTime * tipupWeather;

                FishingPlusLogger.Msg("[FishingPlus] ЖЕРЛИЦА: поклёвка через "
                    + (c.m_MinutesToNextCatch / 60f).ToString("F2") + " ч"
                    + (tipupReason.Length > 0 ? " (событие: " + tipupReason + ")" : ""));
                return;
            }

            // ============================================================
            // УДОЧКА — существующая логика
            // ============================================================
            var holeState = HoleTracker.GetOrCreate(__instance);
            if (holeState.IsResting)
            {
                float rareTime = UnityEngine.Random.Range(DEPLETED_MIN_MINUTES, DEPLETED_MAX_MINUTES);
                c.m_MinutesToNextCatch = rareTime;

                c.m_FishWeightRoll *= DEPLETED_WEIGHT_MULTIPLIER;
                c.m_FishWeightScale *= DEPLETED_WEIGHT_MULTIPLIER;
                c.m_FishMinWeightModifier *= DEPLETED_WEIGHT_MULTIPLIER;

                FishingPlusLogger.Msg("[FishingPlus] ИСТОЩЁННАЯ ЛУНКА ("
                    + holeState.CaughtFish + "/" + holeState.CapacityFish
                    + " рыб). Поклёвка через " + (rareTime / 60f).ToString("F1") + " ч.");
                return;
            }

            float originalTime = c.m_MinutesToNextCatch;

            if (hasLure)
            {
                if (__instance.m_CatchMaxTimeModifier > 1.0f)
                    c.m_MinutesToNextCatch /= __instance.m_CatchMaxTimeModifier;
                c.m_MinutesToNextCatch *= LURE_TIME_MULTIPLIER;
            }

            if (hasBait && hasLure)
            {
                c.m_FishWeightRoll *= SYNERGY_WEIGHT_MULTIPLIER;
                c.m_FishWeightScale *= SYNERGY_WEIGHT_MULTIPLIER;
                c.m_FishMinWeightModifier *= SYNERGY_MIN_WEIGHT_MULTIPLIER;
                c.m_MinutesToNextCatch *= SYNERGY_TIME_MULTIPLIER;
            }

            string reason;                                             // ← оставили как было
            float weatherMultiplier = GetWeatherMultiplier(out reason);
            c.m_MinutesToNextCatch *= weatherMultiplier;

            if (c.m_MinutesToNextCatch < MIN_TIME_MINUTES)
                c.m_MinutesToNextCatch = MIN_TIME_MINUTES;

            string tag = hasBait && hasLure ? "БАТ+ЛЮР" : (hasLure ? "ТОЛЬКО LURE" : "ТОЛЬКО BAIT");
            FishingPlusLogger.Msg("[FishingPlus] " + tag + ": время " + originalTime.ToString("F1")
                + " → " + c.m_MinutesToNextCatch.ToString("F1") + " мин");
            FishingPlusLogger.Msg("[FishingPlus] Факторы: " + reason + " → ×" + weatherMultiplier.ToString("F2"));
        }

        /// <summary>
        /// Жерлица игнорирует обычную погоду, но аврора / мерцающий туман влияют.
        /// </summary>
        private static float GetTipUpWeatherMultiplier(out string reason)
        {
            if (_weather == null) _weather = GameManager.GetWeatherComponent();
            if (_aurora == null) _aurora = GameManager.GetAuroraManager();

            float multiplier = 1.0f;
            string r = "";

            bool auroraActive = false;
            bool electroFog = false;
            try { auroraActive = _aurora != null && _aurora.AuroraIsActive(); } catch { }
            try { electroFog = _weather != null && _weather.IsElectrostaticFog(); } catch { }

            if (auroraActive) { multiplier *= TipUpConfig.AURORA_MULT; r = "aurora"; }
            else if (electroFog) { multiplier *= TipUpConfig.ELECTRO_FOG_MULT; r = "electro-fog"; }

            reason = r;
            return multiplier;
        }

        private static float GetWeatherMultiplier(out string reason)
        {
            if (_weather == null) _weather = GameManager.GetWeatherComponent();
            if (_wind == null) _wind = GameManager.GetWindComponent();
            if (_aurora == null) _aurora = GameManager.GetAuroraManager();
            if (_timeOfDay == null) _timeOfDay = GameManager.GetTimeOfDayComponent();

            if (_weather == null) { reason = "no-data"; return 1.0f; }

            float multiplier = 1.0f;
            string r = "";

            bool auroraActive = false;
            bool electroFog = false;
            try { auroraActive = _aurora != null && _aurora.AuroraIsActive(); } catch { }
            try { electroFog = _weather.IsElectrostaticFog(); } catch { }

            if (auroraActive) { multiplier *= 0.40f; r = "aurora"; }
            else if (electroFog) { multiplier *= 0.45f; r = "electro-fog"; }
            else
            {
                if (_weather.IsBlizzard()) { multiplier *= 3.50f; r = "blizzard"; }
                else if (_weather.IsHeavySnow()) { multiplier *= 2.50f; r = "heavy-snow"; }
                else if (_weather.IsLightSnow()) { multiplier *= 1.00f; r = "light-snow"; }
                else if (_weather.IsDenseFog()) { multiplier *= 1.30f; r = "dense-fog"; }
                else if (_weather.IsFoggy()) { multiplier *= 1.20f; r = "fog"; }
                else if (_weather.IsClear()) { multiplier *= 1.05f; r = "clear"; }
            }

            if (_wind != null)
            {
                try
                {
                    float windIntensity = _wind.GetNormalizedIntensity();
                    if (windIntensity > 0.7f) { multiplier *= 1.80f; r += "+strong-wind"; }
                    else if (windIntensity > 0.4f) { multiplier *= 1.25f; r += "+wind"; }
                    else if (windIntensity < 0.1f) { multiplier *= 0.90f; r += "+calm"; }

                    float angle = _wind.GetWindAngle();
                    if (angle > 135f && angle < 225f) { multiplier *= 0.90f; r += "+south"; }
                    else if ((angle > 315f || angle < 45f) || (angle > 45f && angle < 135f))
                    { multiplier *= 1.10f; r += "+north-east"; }
                }
                catch { }
            }

            try
            {
                float temp = _weather.GetCurrentTemperature();
                if (temp < -20f) { multiplier *= 1.60f; r += "+frost"; }
                else if (temp < -10f) { multiplier *= 1.20f; r += "+cold"; }
                else if (temp > -5f) { multiplier *= 0.90f; r += "+thaw"; }
            }
            catch { }

            if (_timeOfDay != null)
            {
                try
                {
                    if (_timeOfDay.IsDawn()) { multiplier *= 0.85f; r += "+dawn"; }
                    else if (_timeOfDay.IsMorning()) { multiplier *= 0.80f; r += "+morning"; }
                    else if (_timeOfDay.IsDusk()) { multiplier *= 0.85f; r += "+dusk"; }
                    else if (_timeOfDay.IsNight()) { multiplier *= 1.30f; r += "+night"; }
                    else if (_timeOfDay.IsAfternoon()) { multiplier *= 1.00f; r += "+afternoon"; }
                    else if (_timeOfDay.IsDay()) { multiplier *= 0.95f; r += "+day"; }
                }
                catch { }
            }

            reason = r.Length > 0 ? r : "unknown";
            return multiplier;
        }
    }

    // 8. Трекинг улова + правки веса + ФИКС КАЛОРИЙ И ВИТАМИНОВ
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.InstantiateFish))]
    internal static class TrackCaughtFish
    {
        private const float GOLDEYE_MAX_KG = 0.35f;
        private const float GOLDEYE_MIN_KG = 0.10f;
        private const float COHO_FRESHWATER_WEIGHT_MULT = 0.35f;

        private static readonly ItemWeight OneKg = ItemWeight.FromKilograms(1f);

        private static void Postfix(IceFishingHole __instance, GearItem __result)
        {
            if (__result == null) return;
            try
            {
                FishingPlusLogger.Msg("[FishingPlus] Поймана рыба: " + __result.name);
                float oldWeightKg = ToKg(__result.WeightKG); // Вес до бонусов мода

                // #1: Goldeye — мелкая
                if (__result.name == "GEAR_RawGoldeye")
                {
                    float before = ToKg(__result.WeightKG);
                    if (before > GOLDEYE_MAX_KG || before <= 0f)
                    {
                        float capped = UnityEngine.Random.Range(GOLDEYE_MIN_KG, GOLDEYE_MAX_KG);
                        __result.WeightKG = ItemWeight.FromKilograms(capped);
                        FishingPlusLogger.Msg("[FishingPlus] Goldeye: " + before.ToString("F2")
                            + " → " + capped.ToString("F2") + " кг");
                    }
                }

                // #3: Coho в пресной воде — дебафф к весу
                if (__result.name == "GEAR_RawCohoSalmon")
                {
                    bool fresh = false;
                    try { fresh = LootTableHelper.IsFreshwater(__instance.m_LootTable); } catch { }
                    if (fresh)
                    {
                        float before = ToKg(__result.WeightKG);
                        float after = before * COHO_FRESHWATER_WEIGHT_MULT;
                        __result.WeightKG = ItemWeight.FromKilograms(after);
                        FishingPlusLogger.Msg("[FishingPlus] Coho (freshwater): "
                            + before.ToString("F2") + " → " + after.ToString("F2") + " кг");
                    }
                }

                // Жерлица: +100% к размеру
                if (__instance.m_ActiveTipup != null)
                {
                    float before = ToKg(__result.WeightKG);
                    float after = before * TipUpConfig.SIZE_MULT;
                    __result.WeightKG = ItemWeight.FromKilograms(after);
                    FishingPlusLogger.Msg("[FishingPlus] Tip-up size: "
                        + before.ToString("F2") + " → " + after.ToString("F2") + " кг");
                }

                // Златоглазка-наживка: +50% к размеру
                if (__instance.m_SelectedBait != null
                    && __instance.m_SelectedBait.name == "GEAR_RawGoldeye")
                {
                    float before = ToKg(__result.WeightKG);
                    float after = before * TipUpConfig.GOLDEYE_SIZE_MULT;
                    __result.WeightKG = ItemWeight.FromKilograms(after);
                    FishingPlusLogger.Msg("[FishingPlus] Goldeye-bait size: "
                        + before.ToString("F2") + " → " + after.ToString("F2") + " кг");
                }

                // --- ФИНАЛЬНЫЙ ПЕРЕСЧЁТ КАЛОРИЙ И ВИТАМИНОВ (обход лимита игры) ---
                float newWeightKg = ToKg(__result.WeightKG);

                if (__result.m_FoodItem != null && oldWeightKg > 0.001f && newWeightKg > 0.001f)
                {
                    // 1. КАЛОРИИ (базовая плотность)
                    float baseCalorieDensity = __result.m_FoodItem.m_CaloriesRemaining / oldWeightKg;
                    float finalCalories = baseCalorieDensity * newWeightKg;

                    __result.m_FoodItem.m_CaloriesRemaining = finalCalories;
                    __result.m_FoodItem.m_CaloriesTotal = finalCalories;

                    // 2. ВИТАМИНЫ (плотность каждого витамина)
                    // Используем прямую итерацию по Il2CppList, чтобы избежать проблем с конвертацией типов
                    if (__result.m_FoodItem.m_Nutrients != null && __result.m_FoodItem.m_Nutrients.Count > 0)
                    {
                        // Создаем временный чистый C# список для хранения обновленных данных
                        List<FoodItem.Nutrient> updatedNutrients = new List<FoodItem.Nutrient>();

                        // Проходимся по Il2Cpp коллекции
                        foreach (FoodItem.Nutrient nutrient in __result.m_FoodItem.m_Nutrients)
                        {
                            if (nutrient != null && nutrient.m_Amount > 0)
                            {
                                float vitaminPerKg = (float)nutrient.m_Amount / oldWeightKg;
                                float newAmount = vitaminPerKg * newWeightKg;

                                // Меняем значение прямо в существующем объекте
                                nutrient.m_Amount = Mathf.RoundToInt(newAmount);

                                // Добавляем ссылку на тот же объект в наш временный список
                                updatedNutrients.Add(nutrient);
                            }
                        }

                        // Полностью очищаем Il2Cpp список
                        __result.m_FoodItem.m_Nutrients.Clear();

                        // Заполняем его заново обновленными данными
                        foreach (var n in updatedNutrients)
                        {
                            __result.m_FoodItem.m_Nutrients.Add(n);
                        }
                    }

                    FishingPlusLogger.Msg($"[FishingPlus] ФИКС КАЛОРИЙ И ВИТАМИНОВ: {oldWeightKg:F2} кг -> {newWeightKg:F2} кг. " +
                                          $"Итог: {__result.m_FoodItem.m_CaloriesRemaining:F0} ккал");
                }

                HoleTracker.AddCatch(__instance);
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] TrackCaughtFish: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static float ToKg(ItemWeight w)
        {
            return w / OneKg;
        }
    }

    // 9. Реестр «еды-наживки»
    internal static class BaitFoodRegistry
    {
        private static readonly HashSet<string> _baitFoods = new()
        {
            "GEAR_RawMeatDeer", "GEAR_RawMeatRabbit", "GEAR_RawMeatWolf",
            "GEAR_RawMeatBear", "GEAR_RawMeatCougar", "GEAR_RawMeatPtarmigan",
            "GEAR_RawMeatMoose",
            "GEAR_CannedSardines", "GEAR_DogFood", "GEAR_CannedHam",
            "GEAR_CannedCorn", "GEAR_AnimalFat", "GEAR_CuredMeat", "GEAR_CuredFish",
            "GEAR_RawGoldeye",
        };

        public static bool IsBaitFood(string name) => _baitFoods.Contains(name);

        public static void EnsureBaitComponent(GearItem gi)
        {
            try
            {
                if (gi.m_BaitItem != null) return;
                var bait = gi.gameObject.AddComponent<BaitItem>();
                if (bait != null)
                {
                    bait.m_MinFishWeightModifier = 1.0f;
                    gi.m_BaitItem = bait;
                    FishingPlusLogger.Msg("[FishingPlus] Добавлен BaitItem к " + gi.name);
                }
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] EnsureBaitComponent(" + gi.name + "): "
                    + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    // 10. Навешиваем BaitItem на еду при открытии панели
    [HarmonyPatch(typeof(Panel_IceFishing), "RefreshRows")]
    internal static class AddFoodBaitToPanel
    {
        private static void Prefix()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GearItem>();
                int count = 0;
                foreach (var gi in all)
                {
                    if (gi == null) continue;
                    if (!gi.m_InPlayerInventory) continue;
                    if (!BaitFoodRegistry.IsBaitFood(gi.name)) continue;
                    if (gi.m_BaitItem != null) continue;
                    BaitFoodRegistry.EnsureBaitComponent(gi);
                    count++;
                }
                if (count > 0)
                    FishingPlusLogger.Msg("[FishingPlus] Подготовлено еды-наживки: " + count);
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] AddFoodBaitToPanel: "
                    + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    // 11. Расход еды-наживки через калории
    [HarmonyPatch(typeof(IceFishingHole), "ConsumeBait")]
    internal static class ConsumeBaitPatch
    {
        private const float KG_PER_BITE = 0.01f;

        private static bool Prefix(IceFishingHole __instance)
        {
            try
            {
                var gi = __instance.m_SelectedBait;
                if (gi == null) return true;
                if (!BaitFoodRegistry.IsBaitFood(gi.name)) return true;

                var fi = gi.m_FoodItem;
                if (fi == null) return true;

                var fw = gi.m_FoodWeight;
                float calPerKg = (fw != null && fw.m_CaloriesPerKG > 0.01f) ? fw.m_CaloriesPerKG : 1000f;
                float calToRemove = KG_PER_BITE * calPerKg;
                float calBefore = fi.m_CaloriesRemaining;

                if (calBefore <= calToRemove)
                {
                    DestroyFoodBait(__instance, gi, fi);
                    return false;
                }

                fi.m_CaloriesRemaining = calBefore - calToRemove;

                try
                {
                    var newWeight = gi.GetSingleItemWeightKG(false);
                    gi.WeightKG = newWeight;
                }
                catch (Exception e)
                {
                    FishingPlusLogger.Warning("[FishingPlus] WeightKG у " + gi.name
                        + ": " + e.GetType().Name + ": " + e.Message);
                }

                int remainingPortions = (int)(fi.m_CaloriesRemaining / calToRemove);
                FishingPlusLogger.Msg("[FishingPlus] Еда-наживка " + gi.name
                    + ": калории " + calBefore.ToString("F0") + " → "
                    + fi.m_CaloriesRemaining.ToString("F0") + ", осталось ~"
                    + remainingPortions + " порций");

                return false;
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] ConsumeBaitPatch: "
                    + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        private static void DestroyFoodBait(IceFishingHole hole, GearItem gi, FoodItem fi)
        {
            FishingPlusLogger.Msg("[FishingPlus] Еда-наживка " + gi.name + " израсходована — уничтожаем.");
            try { fi.m_CaloriesRemaining = 0f; } catch { }
            try { hole.m_SelectedBait = null; } catch { }
            try { hole.m_SerializedBait = null; } catch { }
            try { UnityEngine.Object.Destroy(gi.gameObject); }
            catch (Exception e) { FishingPlusLogger.Warning("[FishingPlus] Destroy: " + e.Message); }
        }
    }

    // 12. Контекст текущей рыбалки
    internal static class FishingContext
    {
        public static string CurrentBait;
        public static string CurrentLure;

        public static void Clear()
        {
            CurrentBait = null;
            CurrentLure = null;
        }
    }

    // 12.5. Помощник для модификации лут-таблиц
    internal static class LootTableHelper
    {
        private static readonly HashSet<string> FreshwaterFish = new()
        {
            "GEAR_RawGoldeye", "GEAR_RawSmallMouthBass", "GEAR_RawLakeWhiteFish"
        };

        private const string TROUT_GUID = "1c1edd6fd975dca429ba8a801d0d4e3d";
        private const string COHO_GUID = "2ee21c6e70690ab45b48324eee72b21f";

        private static AssetReferenceGearItem _troutRef;
        private static AssetReferenceGearItem _cohoRef;
        private static string _troutGuid;
        private static string _cohoGuid;
        private static bool _warnedMissingRefs;

        public static bool IsFreshwater(FishLootTableData table)
        {
            if (table?.m_BaseEntries == null) return false;
            try
            {
                for (int i = 0; i < table.m_BaseEntries.Count; i++)
                {
                    var name = ResolveName(table.m_BaseEntries[i]);
                    if (name != null && FreshwaterFish.Contains(name)) return true;
                }
            }
            catch { }
            return false;
        }

        public static string ResolveName(RandomTableDataEntry<AssetReferenceGearItem> entry)
        {
            if (entry == null) return null;
            return ResolveName(entry.m_Item);
        }

        public static string ResolveName(AssetReferenceGearItem ar)
        {
            if (ar == null) return null;
            try { var a = ar.Asset; if (a != null) return a.name; } catch { }
            try { var gi = ar.GetOrLoadTypedAsset(); if (gi != null) return gi.name; } catch { }
            try { var go = ar.GetOrLoadAsset(); if (go != null) return go.name; } catch { }
            return null;
        }

        private static void EnsureRefs()
        {
            if (_troutRef != null && _cohoRef != null) return;

            try
            {
                var allTables = Resources.FindObjectsOfTypeAll<FishLootTableData>();
                int tableCount = allTables?.Length ?? 0;
                for (int ti = 0; ti < tableCount; ti++)
                {
                    var t = allTables[ti];
                    if (t == null || t.m_BaseEntries == null) continue;
                    for (int i = 0; i < t.m_BaseEntries.Count; i++)
                    {
                        var entry = t.m_BaseEntries[i];
                        if (entry == null || entry.m_Item == null) continue;
                        var name = ResolveName(entry);
                        if (name == null) continue;
                        if (name == "GEAR_RawRainbowTrout" && _troutRef == null)
                        {
                            _troutRef = entry.m_Item;
                            try { _troutGuid = _troutRef.AssetGUID; } catch { }
                        }
                        else if (name == "GEAR_RawCohoSalmon" && _cohoRef == null)
                        {
                            _cohoRef = entry.m_Item;
                            try { _cohoGuid = _cohoRef.AssetGUID; } catch { }
                        }
                    }
                    if (_troutRef != null && _cohoRef != null) break;
                }
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] EnsureRefs (scan): " + e.Message);
            }

            if (_troutRef == null)
            {
                try
                {
                    _troutRef = new AssetReferenceGearItem(TROUT_GUID);
                    _troutGuid = TROUT_GUID;
                }
                catch (Exception e)
                {
                    FishingPlusLogger.Warning("[FishingPlus] Trout GUID ctor: " + e.Message);
                }
            }

            if (_cohoRef == null)
            {
                try
                {
                    _cohoRef = new AssetReferenceGearItem(COHO_GUID);
                    _cohoGuid = COHO_GUID;
                }
                catch (Exception e)
                {
                    FishingPlusLogger.Warning("[FishingPlus] Coho GUID ctor: " + e.Message);
                }
            }

            if (!_warnedMissingRefs && (_troutRef == null || _cohoRef == null))
            {
                _warnedMissingRefs = true;
                FishingPlusLogger.Warning("[FishingPlus] Refs still missing. Trout="
                    + (_troutRef != null) + " Coho=" + (_cohoRef != null));
            }
        }

        private static bool ContainsGuid(FishLootTableData table, string guid)
        {
            if (table?.m_BaseEntries == null || string.IsNullOrEmpty(guid)) return false;
            for (int i = 0; i < table.m_BaseEntries.Count; i++)
            {
                var ar = table.m_BaseEntries[i]?.m_Item;
                if (ar == null) continue;
                try { if (ar.AssetGUID == guid) return true; } catch { }
            }
            return false;
        }

        public static void EnsureFish(FishLootTableData table)
        {
            if (table?.m_BaseEntries == null) return;
            if (!IsFreshwater(table)) return;

            EnsureRefs();
            if (_troutRef == null && _cohoRef == null) return;

            bool changed = false;
            try
            {
                if (_troutRef != null && !ContainsGuid(table, _troutGuid))
                {
                    var e = new RandomTableDataEntry<AssetReferenceGearItem>
                    {
                        m_Item = _troutRef,
                        m_Weight = 10
                    };
                    table.m_BaseEntries.Add(e);
                    changed = true;
                }

                if (_cohoRef != null && !ContainsGuid(table, _cohoGuid))
                {
                    var e = new RandomTableDataEntry<AssetReferenceGearItem>
                    {
                        m_Item = _cohoRef,
                        m_Weight = 5
                    };
                    table.m_BaseEntries.Add(e);
                    changed = true;
                }

                if (changed) table.FilterEntries();
            }
            catch (Exception ex)
            {
                FishingPlusLogger.Warning("[FishingPlus] EnsureFish: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    // 12.7. Принудительно разрешаем Trout и Coho проходить фильтр в рыбных таблицах
    [HarmonyPatch(typeof(LootTableData), nameof(LootTableData.ShouldFilterOutPrefab))]
    internal static class AllowTroutCohoInFreshwaterPatch
    {
        private static void Postfix(LootTableData __instance, GameObject prefab, ref bool __result)
        {
            if (!__result) return;
            if (prefab == null) return;
            if (!(__instance is FishLootTableData)) return;
            string n = prefab.name;
            if (n == "GEAR_RawRainbowTrout" || n == "GEAR_RawCohoSalmon")
                __result = false;
        }
    }

    // 13. Запоминаем наживку/приманку перед RollNextCatch
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.RollNextCatch))]
    internal static class RollNextCatchContext
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(IceFishingHole __instance)
        {
            try
            {
                FishingContext.CurrentBait = __instance.m_SelectedBait?.name;
                FishingContext.CurrentLure = __instance.m_SelectedLure?.name;
            }
            catch { FishingContext.Clear(); }
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            FishingContext.Clear();
        }
    }

    // 13.5. Гарантируем форель и кижуча в пресных лунках
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.RollNextCatch))]
    internal static class EnsureFreshwaterFishPatch
    {
        [HarmonyPriority(Priority.First - 10)]
        private static void Prefix(IceFishingHole __instance)
        {
            try { LootTableHelper.EnsureFish(__instance.m_LootTable); }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] EnsureFreshwaterFish: " + e.Message);
            }
        }
    }

    // 14. Правила «наживка/приманка → рыба» + региональные бонусы
    internal static class FishBaitRules
    {
        public const float Best = 3.0f;
        public const float Big = 2.5f;
        public const float Medium = 1.8f;
        public const float Normal = 1.0f;
        public const float Small = 0.3f;
        public const float None = 0.0f;

        // ============================================================
        //  РЕГИОНАЛЬНЫЕ БОНУСЫ
        // ============================================================
        private static readonly Dictionary<string, float> RegionBonuses = new()
        {
            ["mountainpassregion"] = 1.5f,   // Sundered Pass
            ["miningregion"] = 0.3f,   // Zone of Contamination

            // Timberwolf Mountain / Hushed River Valley / Ash Canyon — имена пока неизвестны
            ["timberwolfmountain"] = 1.5f,
            ["crashmountain"] = 1.5f,
            ["crashmountainregion"] = 1.5f,
            ["hushedrivervalley"] = 1.5f,
            ["rivervalley"] = 1.5f,
            ["rivervalleyregion"] = 1.5f,
            ["ashcanyon"] = 1.5f,
            ["ashcanyonregion"] = 1.5f,
        };

        private static readonly HashSet<string> _loggedRegions = new();

        private static string NormRegion(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        }

        public static string GetCurrentRegionName()
        {
            try
            {
                string raw = GameManager.TryGetCurrentRegionName();
                if (string.IsNullOrEmpty(raw)) return null;

                if (_loggedRegions.Add(raw))
                {
                    FishingPlusLogger.Msg("[FishingPlus] REGION: '" + raw
                        + "' → norm='" + NormRegion(raw) + "'");
                }
                return raw;
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] GetCurrentRegionName: " + e.Message);
                return null;
            }
        }

        public static float GetRegionBonus(string rawRegionName)
        {
            if (string.IsNullOrEmpty(rawRegionName)) return 1.0f;
            string norm = NormRegion(rawRegionName);
            if (RegionBonuses.TryGetValue(norm, out float b)) return b;
            return 1.0f;
        }

        // ============================================================
        //  ПРАВИЛА СНАСТЕЙ
        // ============================================================
        private static Dictionary<string, float> Meat() => new()
        {
            ["GEAR_RawBurbot"] = Medium,
            ["GEAR_RawCohoSalmon"] = Normal,
            ["GEAR_RawGoldeye"] = None,
            ["GEAR_RawLakeWhiteFish"] = Normal,
            ["GEAR_RawRainbowTrout"] = Normal,
            ["GEAR_RawRedIrishLord"] = Normal,
            ["GEAR_RawRockfish"] = Best,
            ["GEAR_RawSmallMouthBass"] = Best,
        };

        public static readonly Dictionary<string, Dictionary<string, float>> Rules = new()
        {
            ["GEAR_RawMeatDeer"] = Meat(),
            ["GEAR_RawMeatRabbit"] = Meat(),
            ["GEAR_RawMeatWolf"] = Meat(),
            ["GEAR_RawMeatBear"] = Meat(),
            ["GEAR_RawMeatCougar"] = Meat(),
            ["GEAR_RawMeatPtarmigan"] = Meat(),
            ["GEAR_RawMeatMoose"] = Meat(),

            ["GEAR_CannedSardines"] = new()
            {
                ["GEAR_RawBurbot"] = Medium,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = Medium,
                ["GEAR_RawRockfish"] = Best,
                ["GEAR_RawSmallMouthBass"] = None,
            },

            ["GEAR_DogFood"] = new()
            {
                ["GEAR_RawBurbot"] = Big,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = Best,
                ["GEAR_RawRockfish"] = None,
                ["GEAR_RawSmallMouthBass"] = None,
            },

            ["GEAR_CannedHam"] = new()
            {
                ["GEAR_RawBurbot"] = Best,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = None,
                ["GEAR_RawRockfish"] = None,
                ["GEAR_RawSmallMouthBass"] = None,
            },

            ["GEAR_CannedCorn"] = new()
            {
                ["GEAR_RawBurbot"] = None,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = Best,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = None,
                ["GEAR_RawRockfish"] = None,
                ["GEAR_RawSmallMouthBass"] = None,
            },

            ["GEAR_AnimalFat"] = new()
            {
                ["GEAR_RawBurbot"] = Medium,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = Big,
                ["GEAR_RawLakeWhiteFish"] = Big,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = Medium,
                ["GEAR_RawRockfish"] = Medium,
                ["GEAR_RawSmallMouthBass"] = None,
            },

            ["GEAR_CuredMeat"] = Meat(),

            ["GEAR_CuredFish"] = new()
            {
                ["GEAR_RawBurbot"] = Normal,
                ["GEAR_RawCohoSalmon"] = Best,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = Medium,
                ["GEAR_RawRedIrishLord"] = Normal,
                ["GEAR_RawRockfish"] = Normal,
                ["GEAR_RawSmallMouthBass"] = Small,
            },

            ["GEAR_FishingBaitA"] = new()
            {
                ["GEAR_RawBurbot"] = Normal,
                ["GEAR_RawCohoSalmon"] = Medium,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = Medium,
                ["GEAR_RawRedIrishLord"] = Normal,
                ["GEAR_RawRockfish"] = Normal,
                ["GEAR_RawSmallMouthBass"] = Normal,
            },

            ["GEAR_FishingLureA"] = new()
            {
                ["GEAR_RawBurbot"] = Medium,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = Big,
                ["GEAR_RawLakeWhiteFish"] = Big,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = Small,
                ["GEAR_RawRockfish"] = Small,
                ["GEAR_RawSmallMouthBass"] = Small,
            },

            ["GEAR_FishingLureB"] = new()
            {
                ["GEAR_RawBurbot"] = None,
                ["GEAR_RawCohoSalmon"] = None,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = Small,
                ["GEAR_RawRainbowTrout"] = Big,
                ["GEAR_RawRedIrishLord"] = Small,
                ["GEAR_RawRockfish"] = Small,
                ["GEAR_RawSmallMouthBass"] = Big,
            },

            ["GEAR_FishingLureC"] = new()
            {
                ["GEAR_RawBurbot"] = None,
                ["GEAR_RawCohoSalmon"] = Small,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = Big,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = None,
                ["GEAR_RawRockfish"] = Big,
                ["GEAR_RawSmallMouthBass"] = Big,
            },

            ["GEAR_FishingLureD"] = new()
            {
                ["GEAR_RawBurbot"] = None,
                ["GEAR_RawCohoSalmon"] = Best,
                ["GEAR_RawGoldeye"] = Small,
                ["GEAR_RawLakeWhiteFish"] = Small,
                ["GEAR_RawRainbowTrout"] = Best,
                ["GEAR_RawRedIrishLord"] = None,
                ["GEAR_RawRockfish"] = None,
                ["GEAR_RawSmallMouthBass"] = Small,
            },

            // Златоглазка-наживка: только налим, кижуч, малоротый окунь (Best × 1.5)
            ["GEAR_RawGoldeye"] = new()
            {
                ["GEAR_RawBurbot"] = 4.5f,
                ["GEAR_RawCohoSalmon"] = 4.5f,
                ["GEAR_RawSmallMouthBass"] = 4.5f,
                ["GEAR_RawGoldeye"] = None,
                ["GEAR_RawLakeWhiteFish"] = None,
                ["GEAR_RawRainbowTrout"] = None,
                ["GEAR_RawRedIrishLord"] = None,
                ["GEAR_RawRockfish"] = None,
            },
        };

        public static float GetMultiplier(string baitName, string lureName, string fishName)
        {
            float m = Normal;

            if (!string.IsNullOrEmpty(baitName) && Rules.TryGetValue(baitName, out var baitRule))
            {
                if (baitRule.TryGetValue(fishName, out float bm))
                    m *= bm;
                else
                    return None;
            }

            if (!string.IsNullOrEmpty(lureName) && Rules.TryGetValue(lureName, out var lureRule))
            {
                if (lureRule.TryGetValue(fishName, out float lm))
                    m *= lm;
                else
                    return None;
            }

            return m;
        }
    }

    // 15. Применяем правила «наживка/приманка → рыба» + региональные бонусы
    [HarmonyPatch(typeof(FishLootTableData), nameof(FishLootTableData.GetEntryWeight))]
    internal static class FishEntryWeightTweaks
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(RandomTableDataEntry<AssetReferenceGearItem> entry, ref int __result)
        {
            try
            {
                if (entry == null) return;
                if (__result == 0) return;

                string fishName = LootTableHelper.ResolveName(entry);
                if (fishName == null) return;

                string bait = FishingContext.CurrentBait;
                string lure = FishingContext.CurrentLure;
                float m = FishBaitRules.GetMultiplier(bait, lure, fishName);

                string regionName = FishBaitRules.GetCurrentRegionName();
                float regionBonus = FishBaitRules.GetRegionBonus(regionName);
                if (regionBonus != 1.0f) m *= regionBonus;

                int original = __result;
                if (m <= 0f)
                {
                    __result = 0;
                    return;
                }
                if (Math.Abs(m - 1f) > 0.001f)
                {
                    int scaled = (int)(original * m);
                    if (scaled < 1) scaled = 1;
                    __result = scaled;
                }

                FishingPlusLogger.Msg("[FishingPlus] " + fishName + " ×" + m.ToString("F2")
                    + " : " + original + " → " + __result
                    + " (bait=" + (bait ?? "—") + ", lure=" + (lure ?? "—")
                    + ", region=" + (regionName ?? "—") + ")");
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] FishEntryWeightTweaks.Postfix: "
                    + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    // 16. Жерлица увеличивает ёмкость лунки в 2.5 раза (один раз на лунку)
    [HarmonyPatch(typeof(IceFishingHole), nameof(IceFishingHole.SetActiveTipup))]
    internal static class TipUpCapacityPatch
    {
        private static void Postfix(IceFishingHole __instance, GearItem tipupGearItem)
        {
            if (tipupGearItem == null) return;

            try
            {
                var state = HoleTracker.GetOrCreate(__instance);
                if (state.HasHadTipup) return;

                state.HasHadTipup = true;
                int oldCap = state.CapacityFish;
                state.CapacityFish = UnityEngine.Mathf.RoundToInt(oldCap * TipUpConfig.CAPACITY_MULT);
                FishingPlusLogger.Msg("[FishingPlus] Жерлица: ёмкость лунки " + oldCap
                    + " → " + state.CapacityFish + " (+150%)");
            }
            catch (Exception e)
            {
                FishingPlusLogger.Warning("[FishingPlus] TipUpCapacityPatch: " + e.Message);
            }
        }
    }
}