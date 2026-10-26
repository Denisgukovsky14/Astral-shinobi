// Мелочи для русского текста в интерфейсе.
public static class RuText
{
    // Слово «раз» после числа: 1 раз, 2 раза, 5 раз, 11 раз, 21 раз, 22 раза.
    public static string Times(int n)
    {
        int a = n < 0 ? -n : n;
        a %= 100;
        int b = a % 10;
        if (a >= 11 && a <= 14) return "раз";
        return b >= 2 && b <= 4 ? "раза" : "раз";
    }
}
