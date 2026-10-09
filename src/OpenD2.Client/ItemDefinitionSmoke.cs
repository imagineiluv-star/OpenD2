using System.Text;
using OpenD2.Assets;

namespace OpenD2.Client;

// Original-independent fixture for the editor and packaged runtime smoke.
internal static class ItemDefinitionSmoke
{
	public static LegacyItemDefinitionsRequest Request() => new(ItemTables.Profile, [new("TrainingSword", "fws")]);
	public static byte[] Read(string path) => Encoding.ASCII.GetBytes(path.Split('\\', '/')[^1] switch
	{
		"bodylocs.txt" => "Name\tCode\nRight hand\trarm\nTorso\ttors\n",
		"itemtypes.txt" => "ItemType\tCode\tEquiv1\tEquiv2\tBody\tBodyLoc1\tBodyLoc2\nWeapon\tweap\t\t\t1\trarm\t\nArmor\tarmo\t\t\t1\ttors\t\nMisc\tmisc\t\t\t0\t\t\n",
		"weapons.txt" => "name\tcode\tnamestr\ttype\ttype2\tinvwidth\tinvheight\tinvfile\tlevelreq\tstackable\tmindam\tmaxdam\t2handmindam\t2handmaxdam\tminmisdam\tmaxmisdam\treqstr\treqdex\nFixture sword\tfws\tFixtureSword\tweap\t\t1\t3\tfixtureweapon\t2\t0\t7\t13\t0\t0\t0\t0\t12\t8\n",
		"armor.txt" => "name\tcode\tnamestr\ttype\ttype2\tinvwidth\tinvheight\tinvfile\tlevelreq\tstackable\tminac\tmaxac\treqstr\nFixture vest\tfar\tFixtureVest\tarmo\t\t2\t3\tfixturearmor\t1\t0\t3\t8\t10\n",
		"misc.txt" => "name\tcode\tnamestr\ttype\ttype2\tinvwidth\tinvheight\tinvfile\tlevelreq\tstackable\nFixture misc\tfms\tFixtureMisc\tmisc\t\t1\t1\tfixturemisc\t0\t1\n",
		_ => throw new InvalidDataException("Unexpected synthetic item table path.")
	});
}
