using System;

class Program
{
    static int Compute(int x)
    {
        int doubled = x * 2;
        int squared = doubled * x;
        return squared;
    }
    static int Add(int a,int b)
    {
        //Console.
        return a + b;
    } int z = Math.
    static void Main()
    {
        int total = 0;
        for (int i = 1; i <= 3; i++)
        {
            total += Compute(i);
        }
        Console.WriteLine("total=" + total);
        Console.WriteLine("done");
    }
    public static void foo() { }
}
