using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Xamarin.AndroidTools.SchemaGenerator
{
	public class Driver
	{
		public static int Main (string [] args)
		{
			if (args.Length < 1) {
				Console.WriteLine ("USAGE: xamarin-android-schema-generator(.exe) path-to-android-sdk");
				return 1;
			}

			new Driver ().Run (args);

			return 0;
		}

		AndroidSdkStructure sdk;
		IEnumerable<string> additional_libraries;

		public void Run (string [] args)
		{
			this.sdk = AndroidSdkStructure.FromSdkHome (args [0]);
			this.additional_libraries = args.Skip (1);
			var missings = additional_libraries.Where (f => !File.Exists (f));
			if (missings.Any ())
				throw new ArgumentException ($"One or more specified additional libraries do not exist: {string.Join ("", "", missings)}");
				
			Console.WriteLine ($"Android SDK: {sdk.Home}");
			Console.WriteLine ($"Latest platform: {sdk.LatestPlatform}");
			foreach (var extlib in sdk.ExtraLibrariesVersionSpecific)
				Console.WriteLine ($"  Extra: {extlib}");

			foreach (var wi in sdk.Widgets)
				Console.WriteLine ($"  WidgetType: {wi.Kind} {wi.FullName} -extends- {wi.BaseTypeName}");

			foreach (var ds in sdk.DeclaredStyleables)
				Console.WriteLine ($"  Declared Styleable: {ds.Name}");
			
			new SchemaGenerator ().Generate (sdk, "generated");
		}
	}
	
	public class SchemaGenerator
	{
		const string androidNS = "http://schemas.android.com/apk/res/android";
		
		public void Generate (AndroidSdkStructure sdk, string outputDirectory)
		{
			Generate (sdk, Path.Combine (outputDirectory, "android-layout.xsd"), Path.Combine (outputDirectory, "android-attributes.xsd"));
		}
		
		public void Generate (AndroidSdkStructure sdk, string defaultNamespaceFile, string androidNamespaceFile)
		{
			GenerateAndroidAttributesFile (sdk, androidNamespaceFile);
			GenerateDefaultNamespaceFile (sdk, defaultNamespaceFile);
		}
		
		void GenerateDefaultNamespaceFile (AndroidSdkStructure sdk, string defaultNamespaceFile)
		{
			var dir = Path.GetDirectoryName (defaultNamespaceFile);
			if (!Directory.Exists (dir))
				Directory.CreateDirectory (dir);
			
			var xs = new XmlSchema ();
			xs.Namespaces.Add ("android", androidNS);
			
			Func<string,XmlQualifiedName> getTypeQName = s => s == null ? new XmlQualifiedName ("anyType", XmlSchema.Namespace) : new XmlQualifiedName (s + "_Type");
			
			foreach (var vt in sdk.Widgets) {
				var typeName = GetSchemaTypeName (vt.FullName);
				var xe = new XmlSchemaElement {
					Name = typeName,
					SchemaTypeName = getTypeQName (typeName)
				};
				var content = new XmlSchemaComplexContentExtension {
					BaseTypeName = getTypeQName (GetSchemaTypeName (vt.BaseTypeName))
				};
				if (sdk.DeclaredStyleables.Any (ds => ds.Name == typeName))
					content.Attributes.Add (new XmlSchemaAttributeGroupRef { RefName = new XmlQualifiedName (typeName, androidNS) });
				var xt = new XmlSchemaComplexType {
					Name = getTypeQName (typeName).Name,
					ContentModel = new XmlSchemaComplexContent {
						Content = content,
					},
				};
				xs.Items.Add (xe);
				xs.Items.Add (xt);
			}
			using (var writer = XmlWriter.Create (defaultNamespaceFile, new XmlWriterSettings { Indent = true }))
				xs.Write (writer);
		}
		
		string GetSchemaTypeName (string className)
		{
			if (className == null)
				return null;
			var lp = ".LayoutParams";
			bool isLP = className.EndsWith (lp, StringComparison.Ordinal);
			className = isLP ? className.Substring (0, className.Length - lp.Length) : className;
			return className.Substring (className.LastIndexOf ('.') + 1) + (isLP ? lp : null);
		}

		void GenerateAndroidAttributesFile (AndroidSdkStructure sdk, string androidNamespaceFile)
		{
			var xs = new XmlSchema { TargetNamespace = androidNS };
			xs.Namespaces.Add ("android", androidNS);

			foreach (var name in new string [] {"color", "reference", "dimension", "fraction"}) {
				xs.Items.Add (new XmlSchemaSimpleType {
					Name = name,
					Content = new XmlSchemaSimpleTypeRestriction {
						BaseTypeName = new XmlQualifiedName ("string", XmlSchema.Namespace)
					},
				});
			}
			
			// We cannot directly define global attributes within this attributeGroup, because such an attribute will become "local".
			// It first needs to be defined globally and then can be referenced within the attributeGroup.
			// Therefore, those attributes within styleables are first flattened and then added globally.
			var existing = new List<AndroidSdkStructure.Attribute> ();
			foreach (var attr in sdk.DeclaredStyleables.SelectMany (ds => ds.Attributes)) {
				if (existing.Any (e => e.Name == attr.Name))
					// FIXME: check if the content types are equivalent.
					continue;
				existing.Add (attr);
				var est = attr.Enumerations != null && attr.Enumerations.Any () ? new XmlSchemaSimpleType { Name = attr.Name + "_values" } : null;
				if (est != null) {
					var str = new XmlSchemaSimpleTypeRestriction { BaseTypeName = new XmlQualifiedName ("string", XmlSchema.Namespace) };
					est.Content = str;
					foreach (var e in attr.Enumerations)
						str.Facets.Add (new XmlSchemaEnumerationFacet { Value = e.Name });
					xs.Items.Add (est);
				}
				var ad = new XmlSchemaAttribute {
					Name = attr.Name,
					// FIXME: handle union of formats,
					SchemaTypeName = attr.Enumerations.Any () || !attr.Formats.Any () ? null : new XmlQualifiedName (attr.Formats [0].XsdType),
					SchemaType = attr.Enumerations.Any () ?
						new XmlSchemaSimpleType {
							Content = attr.Formats.FirstOrDefault ()?.Name == "enum" ?
								(XmlSchemaSimpleTypeContent)
								new XmlSchemaSimpleTypeRestriction {
									BaseTypeName = new XmlQualifiedName (est.Name, androidNS)
								} :
								new XmlSchemaSimpleTypeList {
									ItemTypeName = new XmlQualifiedName (est.Name, androidNS)
								}
						} : null,
				};
				xs.Items.Add (ad);
			}
			foreach (var ds in sdk.DeclaredStyleables) {
				var ag = new XmlSchemaAttributeGroup {
					Name = ds.Name ?? "__global__",
				};
				foreach (var attr in ds.Attributes) {
					ag.Attributes.Add (new XmlSchemaAttribute { RefName = new XmlQualifiedName (attr.Name, androidNS)});
				}
				xs.Items.Add (ag);
			}
			using (var writer = XmlWriter.Create (androidNamespaceFile, new XmlWriterSettings { Indent = true }))
				xs.Write (writer);
		}
	}
	
	public class AndroidSdkStructure
	{
		public static AndroidSdkStructure FromSdkHome (string home)
		{
				return new AndroidSdkStructure (home);
		}
		
		AndroidSdkStructure (string home)
		{
			this.Home = home;
			
			if (!Directory.Exists (home))
				throw new ArgumentException ($"Android SDK does not exist at the specified path: '{home}'");

			var apiLevel = Directory.GetDirectories (Path.Combine (Home, "platforms"), "android-*")
				.Where (d =>
					File.Exists (Path.Combine (d, "android.jar")) &&
					Directory.Exists (Path.Combine (d, "data", "res")))
				.Select (d => Path.GetFileName (d))
				.Where (p => p.StartsWith ("android-", StringComparison.Ordinal))
				.Select (p => p.Substring (8))
				.OrderBy (l => int.TryParse (l, out int ddd) ? ddd : -1)
				.LastOrDefault ();
			if (apiLevel == null)
				throw new ArgumentException ($"The specified directory '{home}' does not contain any Android SDK platform (which should contain 'android.jar' and 'data/res' subdirectory).");
			this.LatestPlatform = Path.Combine (Home, "platforms", "android-" + apiLevel);
			
			this.android_jar = Path.Combine (LatestPlatform, "android.jar");
			
			var data_dir = Path.Combine (LatestPlatform, "data");
			this.widgets_txt = Path.Combine (data_dir, "widgets.txt");
			this.res_dir = Path.Combine (data_dir, "res");
			
			ExtraLibrariesVersionSpecific = GetExtraLibraryDirectories ().ToList ();
			
			Widgets = GetWidgetsParsed ().ToList ();
			DeclaredStyleables = GetDeclaredStyleables ();
		}
		
		string android_jar, widgets_txt, res_dir;

		public string Home { get; set; }		
		public string LatestPlatform { get; set; }
		public IList<string> ExtraLibrariesVersionSpecific { get; set; }
		
		// extras
		
		IEnumerable<string> GetExtraLibraryDirectories ()
		{
			var extraAllSubdirs = Directory.GetDirectories (Path.Combine (Home, "extras"), "*", SearchOption.AllDirectories);
			var extraLibsVersionSpecific =
				extraAllSubdirs
				.Select (d => Path.Combine (d, "maven-metadata.xml"))
				.Where (f => File.Exists (f))
				.Select (f => Path.Combine (Path.GetDirectoryName (f), GetReleaseVersionFromMavenMetadataFile (f)));
			return extraLibsVersionSpecific;
		}
		
		string GetReleaseVersionFromMavenMetadataFile (string file)
		{
			var doc = XDocument.Load (file);
			var md = doc.Element ("metadata");
			var ver = md.Element ("versioning");
			var rel = ver.Element ("release");
			if (rel != null)
				return rel.Value;
			return ver.Element ("versions").Elements ("version").Last ().Value;
		}

		// widgets.txt

		public IList<TypeInformation> Widgets { get; set; }

		public enum ViewKindEvaluation
		{
			ToBeDetermined,
			Widget,
			Layout,
			LayoutParams,
			Other,
		}
		
		public class TypeInformation
		{
			public string FullName { get; set; }
			public string BaseTypeName { get; set; }
			public ViewKindEvaluation Kind { get; set; }
		}
		
		IEnumerable<TypeInformation> GetWidgetsParsed ()
		{
			var types = new List<TypeInformation> ();
			foreach (var line in File.ReadAllLines (widgets_txt).Where (l => l.Length > 0)) {
				var items = line.Substring (1).Split (' ');
				for (int i = 0; i < items.Length - 1; i++) {
					if (types.Any (t => t.FullName == items [i]))
						break;
					types.Add (new TypeInformation {
						Kind = GetViewKind (line [0]),
						FullName = items [i],
						BaseTypeName = items [i + 1] == "java.lang.Object" ? null : items [i + 1]
					});
				}
			}
			return types;
		}
		
		ViewKindEvaluation GetViewKind (char c)
		{
			switch (c) {
			case 'L': return ViewKindEvaluation.Layout;
			case 'P': return ViewKindEvaluation.LayoutParams;
			case 'W': return ViewKindEvaluation.Widget;
			}
			return ViewKindEvaluation.Other;
		}

		// attrs.xml
		
		public class Attribute
		{
			public string Name { get; set; }
			public IList<AttributeFormat> Formats { get; set; }
			public IList<AttributeValueEnumeration> Enumerations { get; set; }
			
			public static Attribute Load (XElement e)
			{
				return new Attribute {
					Name = e.Attribute ("name")?.Value,
					Formats = AttributeFormat.Get (e.Attribute ("format")?.Value).ToList (),
					Enumerations = e.Elements ("flag").Concat (e.Elements ("enum")).Select (c => AttributeValueEnumeration.Load (c)).ToList ()
				};
			}
		}
		
		public class AttributeFormat
		{
			public static IEnumerable<AttributeFormat> Get (string names)
			{
				if (names == null)
					yield break;
				foreach (var name in names.Split ('|')) {
					var ret = formats.FirstOrDefault (f => f.Name == name);
					if (ret == null)
						throw new ArgumentException ($"Unexpected format name was requested: {name}");
					yield return ret;
				}
			}
			
			public static readonly AttributeFormat None = new AttributeFormat { Name = null, XsdType = null };
			
			static IList<AttributeFormat> formats;
			
			static AttributeFormat ()
			{
				formats = new AttributeFormat [] {
					new AttributeFormat { Name = "boolean", XsdType = "xs:boolean" },
					new AttributeFormat { Name = "color", XsdType = "android:color" },
					new AttributeFormat { Name = "dimension", XsdType = "android:dimension" },
					new AttributeFormat { Name = "enum", XsdType = null },
					new AttributeFormat { Name = "float", XsdType = "xs:float" },
					new AttributeFormat { Name = "fraction", XsdType = "android:fraction" },
					new AttributeFormat { Name = "integer", XsdType = "xs:integer" },
					new AttributeFormat { Name = "reference", XsdType = "android:reference" },
					new AttributeFormat { Name = "string", XsdType = "xs:string" },
				};
			}
			
			public string Name { get; set; }
			public string XsdType { get; set; }
		}
		
		public class AttributeValueEnumeration
		{
			public string Name { get; set; }
			public string Value { get; set; }
			
			public static AttributeValueEnumeration Load (XElement e)
			{
				return new AttributeValueEnumeration {
					Name = e.Attribute ("name")?.Value,
					Value = e.Attribute ("value")?.Value
				};
			}
		}
		
		public class Styleable
		{
			public string Name { get; set; }
			public IList<Attribute> Attributes { get; set; }
			
			public static Styleable Load (XElement e)
			{
				return new Styleable {
					Name = e.Attribute ("name")?.Value,
					Attributes = e.Elements ("attr").Select (c => Attribute.Load (c)).ToList ()
				};
			}
		}
		
		public IList<Styleable> DeclaredStyleables { get; set; }
		
		public IList<Styleable> GetDeclaredStyleables ()
		{
			var doc = XDocument.Load (Path.Combine (res_dir, "values", "attrs.xml"));
			var ret = doc.Element ("resources")
				.Elements ("declare-styleable")
				.Select (e => Styleable.Load (e))
				.ToList ();
			ret.Add (Styleable.Load (doc.Root));
			
			return ret;
		}
	}
}

