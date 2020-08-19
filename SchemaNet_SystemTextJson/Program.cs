using Schema.NET;
using System;
using System.Text.Json.Serialization;

namespace SchemaNet_SystemTextJson
{
	public class CustomType : Thing, IThing
	{
		public override string Type => "CustomType";

		[JsonPropertyName("name")]
		[JsonConverter(typeof(OneOrManyJsonConverter<string>))]
		public OneOrMany<string> Name { get; set; }

		[JsonPropertyName("uri")]
		[JsonConverter(typeof(ValuesJsonConverter<string, Uri>))]
		public Values<string, Uri> Uri { get; set; }
	}

	class Program
	{
		static void Main(string[] args)
		{
			// {"@context":"https://schema.org","@type":"CustomType","Name":"Hello World"}
			var inputObj = new CustomType
			{
				Name = new[] { "Hello ", "World" },
				Uri = new Uri("http://localhost"),
			};

			var json = SchemaSerializer.SerializeObject(inputObj);

			var outputObj = SchemaSerializer.DeserializeObject<CustomType>(json);
		}
	}
}
