using AsepriteManager;

var cases = new (string installed, string latest, int expected)[]
{
    ("v1.3.18.2-dev", "v1.3.18.2", 0),
    ("1.3.18.2-dev", "v1.3.18.6", -1),
    ("1.3.18.6", "1.3.18.6", 0),
    ("1.3.19", "1.3.18.6", 1),
    ("1.3.18.6-beta1", "1.3.18.6", -1),
    ("1.3.18.6", "1.3.18.6-beta1", 1),
    ("1.3.18.6", "1.3.18.7", -1),
    ("1.3.18.6", "1.3.19", -1)
};
foreach (var (installed, latest, expected) in cases)
{
    var actual = Math.Sign(VersionOrder.Compare(installed, latest));
    if (actual != expected) throw new Exception($"FAIL: {installed} vs {latest}: expected {expected}, got {actual}");
    Console.WriteLine($"PASS: {installed} vs {latest}");
}
Console.WriteLine("All version comparison checks passed.");
