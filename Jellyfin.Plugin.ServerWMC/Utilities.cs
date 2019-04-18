using System;

namespace wmc2mb
{
    static class Utilities
    {
        // used for time_t conversion
        static readonly DateTimeOffset TIME_T_REF = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        // smallest datetime value allowed
        static readonly DateTimeOffset TIME_MIN = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static DateTimeOffset ToDateTime(string time_t)
        {
            long t = long.Parse(time_t);
            return TIME_T_REF.AddSeconds(t);
        }

        /// <summary>
        /// convert from DateTime to c++ time_t (in string form) 
        /// </summary>
        /// <param name="dt">DateTime to convert</param>
        /// <returns>output time_t in string form</returns>
        public static string ToTime_t(DateTimeOffset dt)
        {
            if (dt < TIME_MIN)
                dt = TIME_MIN;
            TimeSpan ts = dt.Subtract(TIME_T_REF);
            long time_t = (long)(ts.TotalSeconds);
            return time_t.ToString();
        }

        /// <summary>
        /// return an int? based on from input string, returns null if string can't be parse to an int
        /// </summary>
        /// <param name="istr">string to parse</param>
        /// <param name="zeroToNull">if true, output null if integer value is zero</param>
        /// <returns>int if possible to parse string, null otherwise</returns>
        public static int? ToInt(string istr, bool zeroToNull = false)
        {
            int num;
            if (string.IsNullOrEmpty(istr))
                return null;
            else if (int.TryParse(istr, out num))
            {
                if (num == 0 && zeroToNull)             // if value is zero and we want to map zeros to null, output null
                    return null;
                else
                    return num;
            }
            else
                return null;
        }
    }
}
