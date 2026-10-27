using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Harley
{
    /// <summary>
    /// Harley's internet tools, offered to Claude on every message:
    ///  - web_search: news, sports, prices, anything recent (Claude's web search tool)
    ///  - get_weather: live weather and 3-day forecast (Open-Meteo, free, no key)
    /// Claude decides when to use them.
    ///
    /// Where you are: Quest has no GPS, so if Home City is empty in the config,
    /// Harley guesses your city from your internet connection once per session
    /// (ipwho.is, free). It's approximate (often the nearest bigger city).
    /// </summary>
    public class CompanionTools : MonoBehaviour
    {
        public CompanionConfig config;
        public ClaudeClient claude;
        [Tooltip("Guess the player's city from the internet connection when Home City is empty.")]
        public bool detectLocation = true;

        /// <summary>City guessed from the internet connection, e.g. "Darmstadt, Hesse (DE)". Empty until known.</summary>
        public string DetectedPlace { get; private set; } = "";
        public string DetectedCity { get; private set; } = "";
        public string DetectedCountryCode { get; private set; } = "";
        double _detLat, _detLon;
        bool _detecting;

        double _cachedLat, _cachedLon;
        string _cachedPlace, _cachedLabel;

        IEnumerator Start()
        {
            if (detectLocation && config != null && string.IsNullOrWhiteSpace(config.homeCity)
                && Mathf.Abs(config.homeLatitude) < 0.001f && Mathf.Abs(config.homeLongitude) < 0.001f)
                yield return DetectLocation();
        }

        /// <summary>Approximate location from the internet connection (no GPS on Quest).</summary>
        public IEnumerator DetectLocation()
        {
            if (_detecting || DetectedPlace.Length > 0) yield break;
            _detecting = true;
            Dictionary<string, object> r = null;
            yield return GetJson("https://ipwho.is/", d => r = d, _ => { });
            if (r != null && r.TryGetValue("success", out var ok) && ok is bool b && b && !string.IsNullOrEmpty(r.Str("city")))
            {
                _detLat = r.Num("latitude");
                _detLon = r.Num("longitude");
                DetectedCity = r.Str("city");
                DetectedCountryCode = r.Str("country_code") ?? "";
                DetectedPlace = DetectedCity + ", " + (r.Str("region") ?? "") + " (" + DetectedCountryCode + ")";
            }
            else
            {
                // Fallback service: only use it if it knows the city.
                Dictionary<string, object> g = null;
                yield return GetJson("https://get.geojs.io/v1/ip/geo.json", d => g = d, _ => { });
                if (g != null && !string.IsNullOrEmpty(g.Str("city")))
                {
                    _detLat = g.Num("latitude");
                    _detLon = g.Num("longitude");
                    DetectedCity = g.Str("city");
                    DetectedCountryCode = g.Str("country_code") ?? "";
                    DetectedPlace = DetectedCity + ", " + (g.Str("region") ?? "") + " (" + DetectedCountryCode + ")";
                }
            }
            _detecting = false;
        }

        public List<object> Definitions()
        {
            var list = new List<object>();
            if (config == null) return list;

            if (config.webSearch)
                list.Add(MiniJson.Obj(
                    "name", "web_search",
                    "description", "Search the internet for current or recent information: news, sports results, prices, " +
                                   "release dates, opening hours, facts you are not sure about, anything after your knowledge cutoff.",
                    "input_schema", MiniJson.Obj(
                        "type", "object",
                        "properties", MiniJson.Obj("query", MiniJson.Obj("type", "string", "description", "What to look up, as a short search phrase.")),
                        "required", new List<object> { "query" })));

            if (config.weather)
                list.Add(MiniJson.Obj(
                    "name", "get_weather",
                    "description", "Current weather and a 3-day forecast. Leave place empty for the player's home.",
                    "input_schema", MiniJson.Obj(
                        "type", "object",
                        "properties", MiniJson.Obj("place", MiniJson.Obj("type", "string",
                            "description", "City in English (Latin letters), optionally with region or country, e.g. \"Nazareth, Israel\" or " +
                                           "\"Münster, Hessen\". Always use the English name, even when the player speaks another language. Empty = player's home.")),
                        "required", new List<object>())));

            return list;
        }

        public IEnumerator Run(string name, Dictionary<string, object> input, Action<string> done)
        {
            switch (name)
            {
                case "web_search":
                    if (claude == null) { done("Search is not available."); yield break; }
                    yield return claude.SearchWeb(input.Str("query") ?? "", done, DetectedCity, DetectedCountryCode);
                    break;
                case "get_weather":
                    yield return Weather(input.Str("place"), done);
                    break;
                default:
                    done("Unknown tool: " + name);
                    break;
            }
        }

        // ---------- weather ----------

        IEnumerator Weather(string place, Action<string> done)
        {
            bool home = string.IsNullOrWhiteSpace(place);
            if (home) place = config.homeCity;

            double lat = 0, lon = 0;
            string label = place;

            if (home && (Mathf.Abs(config.homeLatitude) > 0.001f || Mathf.Abs(config.homeLongitude) > 0.001f))
            {
                lat = config.homeLatitude;
                lon = config.homeLongitude;
                if (string.IsNullOrWhiteSpace(label)) label = "home";
            }
            else if (home && string.IsNullOrWhiteSpace(place))
            {
                // No city in the config: use the location from the internet connection.
                if (DetectedPlace.Length == 0 && detectLocation) yield return DetectLocation();
                if (DetectedPlace.Length == 0)
                {
                    done("I couldn't tell where the player is. Ask them which city they mean.");
                    yield break;
                }
                lat = _detLat;
                lon = _detLon;
                label = DetectedPlace + " (guessed from the internet connection)";
            }
            else if (string.IsNullOrWhiteSpace(place))
            {
                done("Ask the player which city they mean.");
                yield break;
            }
            else if (place == _cachedPlace)
            {
                lat = _cachedLat; lon = _cachedLon; label = _cachedLabel;
            }
            else
            {
                string[] parts = place.Split(',');
                string name = parts[0].Trim();
                string hint = parts.Length > 1 ? parts[1].Trim() : (home ? config.countryCode : "");
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name=" + UnityWebRequest.EscapeURL(name);
                Dictionary<string, object> geo = null;
                string err = null;
                yield return GetJson(url, r => geo = r, e => err = e);
                if (err != null) { done("Weather lookup failed: " + err); yield break; }

                var results = geo.ListAt("results");
                Dictionary<string, object> pick = null;
                if (results != null)
                {
                    foreach (var o in results)
                    {
                        if (!(o is Dictionary<string, object> r)) continue;
                        if (pick == null) pick = r;
                        if (hint.Length > 0 && Matches(r, hint)) { pick = r; break; }
                    }
                }
                if (pick == null) { done("No place called \"" + place + "\" was found. Try again with the English name of the city, optionally with its country."); yield break; }
                lat = pick.Num("latitude");
                lon = pick.Num("longitude");
                label = pick.Str("name") + ", " + (pick.Str("admin1") ?? "") + " (" + (pick.Str("country_code") ?? "") + ")";
                _cachedPlace = place; _cachedLat = lat; _cachedLon = lon; _cachedLabel = label;
            }

            string f = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.####", CultureInfo.InvariantCulture) +
                       "&longitude=" + lon.ToString("0.####", CultureInfo.InvariantCulture) +
                       "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,precipitation" +
                       "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
                       "&timezone=auto&forecast_days=3";
            Dictionary<string, object> w = null;
            string werr = null;
            yield return GetJson(f, r => w = r, e => werr = e);
            if (werr != null) { done("Weather lookup failed: " + werr); yield break; }

            var sb = new StringBuilder();
            sb.Append("Weather for ").Append(label).Append(". ");
            var cur = w.ObjAt("current");
            if (cur != null)
            {
                sb.Append("Now: ").Append(Round(cur.Num("temperature_2m"))).Append("°C (feels like ")
                  .Append(Round(cur.Num("apparent_temperature"))).Append("°C), ")
                  .Append(Describe((int)cur.Num("weather_code", -1))).Append(", wind ")
                  .Append(Round(cur.Num("wind_speed_10m"))).Append(" km/h, humidity ")
                  .Append(Round(cur.Num("relative_humidity_2m"))).Append("%. ");
            }
            var daily = w.ObjAt("daily");
            if (daily != null)
            {
                var days = daily.ListAt("time");
                var codes = daily.ListAt("weather_code");
                var max = daily.ListAt("temperature_2m_max");
                var min = daily.ListAt("temperature_2m_min");
                var rain = daily.ListAt("precipitation_probability_max");
                string[] names = { "Today", "Tomorrow", "Day after tomorrow" };
                for (int i = 0; days != null && i < days.Count && i < 3; i++)
                {
                    sb.Append(names[i]).Append(": ")
                      .Append(Describe((int)At(codes, i, -1))).Append(", ")
                      .Append(Round(At(min, i))).Append(" to ").Append(Round(At(max, i))).Append("°C");
                    double p = At(rain, i, double.NaN);
                    if (!double.IsNaN(p)) sb.Append(", rain chance ").Append(Round(p)).Append('%');
                    sb.Append(". ");
                }
            }
            done(sb.ToString().Trim());
        }

        static bool Matches(Dictionary<string, object> r, string hint)
        {
            string h = hint.ToLowerInvariant();
            foreach (var key in new[] { "admin1", "admin2", "admin3", "country", "country_code" })
            {
                string v = r.Str(key);
                if (string.IsNullOrEmpty(v)) continue;
                v = v.ToLowerInvariant();
                if (v.Contains(h) || h.Contains(v)) return true; // "Hessen" vs "Hesse"
            }
            return false;
        }

        static double At(List<object> list, int i, double fallback = 0)
        {
            if (list == null || i >= list.Count || list[i] == null) return fallback;
            if (list[i] is double d) return d;
            if (list[i] is long l) return l;
            return fallback;
        }

        static string Round(double v) => double.IsNaN(v) ? "?" : Math.Round(v).ToString(CultureInfo.InvariantCulture);

        // WMO weather codes used by Open-Meteo
        static string Describe(int code)
        {
            switch (code)
            {
                case 0: return "clear sky";
                case 1: return "mainly clear";
                case 2: return "partly cloudy";
                case 3: return "overcast";
                case 45: case 48: return "fog";
                case 51: case 53: case 55: return "drizzle";
                case 56: case 57: return "freezing drizzle";
                case 61: return "light rain";
                case 63: return "rain";
                case 65: return "heavy rain";
                case 66: case 67: return "freezing rain";
                case 71: return "light snow";
                case 73: return "snow";
                case 75: return "heavy snow";
                case 77: return "snow grains";
                case 80: case 81: return "rain showers";
                case 82: return "heavy rain showers";
                case 85: case 86: return "snow showers";
                case 95: return "thunderstorm";
                case 96: case 99: return "thunderstorm with hail";
                default: return "unknown conditions";
            }
        }

        static IEnumerator GetJson(string url, Action<Dictionary<string, object>> ok, Action<string> fail)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { fail(req.error); yield break; }
                Dictionary<string, object> d = null;
                try { d = MiniJson.Parse(req.downloadHandler.text) as Dictionary<string, object>; }
                catch (Exception e) { fail(e.Message); yield break; }
                if (d == null) { fail("empty response"); yield break; }
                if (d.TryGetValue("error", out var isErr) && isErr is bool b && b) { fail(d.Str("reason") ?? "error"); yield break; }
                ok(d);
            }
        }
    }
}
