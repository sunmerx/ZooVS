using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace DebugTarget
{
    internal class Class1
    {
        public Class1() {
            Program.foo();
        }
        public int Add(int a,int b)
        {
            Console.WriteLine(a + b);
            Console.ReadLine();
            return a + b;
        }
        public int Subtract(int a, int b)
        {
            Console.WriteLine(a - b);
            return a - b;
        }
        public int Mul(int a,int b)
        {
            Console.WriteLine(a * b);
            return a * b;
        }
        
        public int Dev(int a,int b)
        {
            Console.WriteLine(a / b);
            return a / b;
        }
        /// <summary>
        /// 锟斤拷锟斤拷圆锟斤拷锟斤拷 锟斤拷 锟斤拷 20 位小锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷 "3.14159265358979323846" 锟斤拷锟街凤拷锟斤拷锟斤拷
        /// 使锟斤拷 Machin 锟斤拷式锟斤拷锟斤拷/4 = 4锟斤拷arctan(1/5) - arctan(1/239)锟斤拷+ BigInteger 锟斤拷锟斤拷锟斤拷锟斤拷锟斤拷锟姐，
        /// 锟斤拷锟斤拷锟斤拷锟?10 位锟斤拷锟斤拷位锟斤拷囟希锟斤拷锟街わ拷锟?20 位小锟斤拷锟斤拷确锟斤拷
        /// </summary>
        public static string ComputePi20Digits()
        {
            
            const int digits = 20;      // 锟斤拷要锟斤拷小锟斤拷位锟斤拷
            const int guard = 10;       // 锟斤拷锟斤拷位锟斤拷锟斤拷锟秸硷拷锟斤拷锟截讹拷锟斤拷锟?

            int target = digits + guard;
            BigInteger scale = BigInteger.Pow(10, target);

            // 锟斤拷 锟斤拷 4 * (4 * arctan(1/5) - arctan(1/239))
            BigInteger pi = 4 * (4 * ArctanInv(5, scale) - ArctanInv(239, scale));

            // 锟斤拷锟斤拷锟斤拷锟斤拷位锟斤拷只锟斤拷锟斤拷 20 位小锟斤拷
            BigInteger scaled = pi / BigInteger.Pow(10, guard);

            BigInteger integerPart = scaled / scale;
            BigInteger fractionPart = scaled % scale;

            string fraction = fractionPart.ToString().PadLeft(digits, '0');
            return integerPart + "." + fraction;
        }

        /// <summary>
        /// 锟斤拷锟斤拷 arctan(1/x) 锟斤拷锟斤拷 <paramref name="scale"/> 锟脚达拷为锟斤拷锟斤拷锟斤拷
        /// Machin 锟斤拷锟斤拷锟斤拷arctan(1/x) = 1/x - 1/(3x^3) + 1/(5x^5) - 1/(7x^7) + ...
        /// </summary>
        private static BigInteger ArctanInv(int x, BigInteger scale)
        {
            BigInteger xSquared = (BigInteger)x * x;
            BigInteger term = scale / x;   // 锟斤拷锟斤拷 1/x
            BigInteger sum = term;
            int n = 1;
            bool subtractNext = true;

            while (true)
            {
                term /= xSquared;          // 锟斤拷锟狡碉拷锟斤拷一锟斤拷
                if (term == 0) break;

                n += 2;                    // 锟斤拷母锟斤拷锟斤拷 3, 5, 7, ...
                BigInteger current = term / n;

                if (subtractNext) sum -= current;
                else sum += current;

                subtractNext = !subtractNext;
            }

            return sum;
        }

        /// <summary>
        /// 计算从坐标点 (x1, y1) 指向坐标点 (x2, y2) 的向量与 X 轴正方向之间的夹角（弧度）。
        /// 采用数学坐标系惯例：逆时针方向为正。
        /// x1、y1 为起点坐标，x2、y2 为终点坐标。
        /// 返回值为夹角弧度，取值范围 (-π, π]；两点重合时返回 0。
        /// </summary>
        public static double GetAngle(double x1, double y1, double x2, double y2)
        {
            return Math.Atan2(y2 - y1, x2 - x1);
        }

        /// <summary>
        /// 计算从坐标点 from 指向坐标点 to 的向量与 X 轴正方向之间的夹角（弧度）。
        /// 采用数学坐标系惯例：逆时针方向为正。
        /// 返回值为夹角弧度，取值范围 (-π, π]；两点重合时返回 0。
        /// </summary>
        public static double GetAngle(Vector2 from, Vector2 to)
        {
            return Math.Atan2(to.Y - from.Y, to.X - from.X);
        }

        /// <summary>
        /// 计算从坐标点 (x1, y1) 指向坐标点 (x2, y2) 的向量与 X 轴正方向之间的夹角（角度制）。
        /// x1、y1 为起点坐标，x2、y2 为终点坐标。
        /// 返回值为夹角角度，取值范围 [0, 360)；两点重合时返回 0。
        /// </summary>
        public static double GetAngleDegrees(double x1, double y1, double x2, double y2)
        {
            double degrees = GetAngle(x1, y1, x2, y2) * 180.0 / Math.PI;
            return degrees < 0 ? degrees + 360.0 : degrees;
        }
    }
}

