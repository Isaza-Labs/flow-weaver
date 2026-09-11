using System.Text.Json;
using flow_weaver_backend.Services.Ai.Tools;

namespace flow_weaver_backend.Tests;

// Vendor-generated specs define every parameter, body schema and response once
// under `components` and `$ref` them from each operation. The slicer used to read
// a `$ref` entry as a nameless parameter and show such operations with no
// parameters at all — so the model never learned that `limit` and `from` existed
// and took the server's 50-row default. These pin the resolution.
public class OperationYamlSlicerRefTests
{
    private const string Yaml = """
        openapi: 3.0.3
        info:
          title: refs
          version: '1.0'
        paths:
          "/endpoints/managed/{orgId}":
            parameters:
              - "$ref": "#/components/parameters/orgId"
            get:
              operationId: endpoints_managed
              parameters:
                - "$ref": "#/components/parameters/limit"
                - "$ref": "#/components/parameters/from"
                - "$ref": "#/components/parameters/orgId"
                - "$ref": "#/components/parameters/does_not_exist"
                - name: inline
                  in: query
                  schema:
                    type: string
              responses:
                "200":
                  "$ref": "#/components/responses/EndpointsResultPage"
            post:
              operationId: endpoints_create
              requestBody:
                "$ref": "#/components/requestBodies/EndpointBody"
              responses:
                "201":
                  description: Created
        components:
          parameters:
            orgId:
              name: orgId
              in: path
              required: true
              schema:
                type: string
            limit:
              name: limit
              in: query
              description: Page size.
              schema:
                type: number
                minimum: 1
            from:
              name: from
              in: query
              description: Offset of the first record.
              schema:
                type: integer
          schemas:
            ResultPage:
              type: object
              properties:
                items:
                  type: array
                total_items:
                  type: string
            Endpoint:
              type: object
              properties:
                name:
                  type: string
                comment:
                  type: string
          requestBodies:
            EndpointBody:
              required: true
              content:
                application/json:
                  schema:
                    "$ref": "#/components/schemas/Endpoint"
          responses:
            EndpointsResultPage:
              description: A ResultPage of Endpoint objects.
              content:
                application/json:
                  schema:
                    allOf:
                      - "$ref": "#/components/schemas/ResultPage"
                    properties:
                      next_page:
                        type: string
        """;

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Referenced_parameters_are_resolved_and_deduped_with_the_path_level_ones()
    {
        var slice = OperationYamlSlicer.ExtractDetail(Yaml, "get", "/endpoints/managed/{orgId}");

        var names = slice.Parameters.Select(p => Json(p).GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "orgId", "limit", "from", "inline" }, names);

        var limit = Json(slice.Parameters.Single(p => Json(p).GetProperty("name").GetString() == "limit"));
        Assert.Equal("query", limit.GetProperty("in").GetString());
        Assert.Equal("Page size.", limit.GetProperty("description").GetString());
        Assert.Equal("number", limit.GetProperty("schema").GetProperty("type").GetString());

        var orgId = Json(slice.Parameters.Single(p => Json(p).GetProperty("name").GetString() == "orgId"));
        Assert.True(orgId.GetProperty("required").GetBoolean());
    }

    // A dangling reference is not a parameter; it must not surface as a nameless
    // entry, and it must not stop the rest from resolving.
    [Fact]
    public void An_unresolvable_reference_is_skipped()
    {
        var slice = OperationYamlSlicer.ExtractDetail(Yaml, "get", "/endpoints/managed/{orgId}");

        Assert.DoesNotContain(slice.Parameters, p => Json(p).GetProperty("name").GetString() == "");
    }

    [Fact]
    public void Referenced_response_gives_the_preview()
    {
        var slice = OperationYamlSlicer.ExtractDetail(Yaml, "get", "/endpoints/managed/{orgId}");

        Assert.Equal("A ResultPage of Endpoint objects.", slice.ResponsePreview);
    }

    [Fact]
    public void Referenced_request_body_and_schema_are_resolved()
    {
        var slice = OperationYamlSlicer.ExtractDetail(Yaml, "post", "/endpoints/managed/{orgId}");

        Assert.NotNull(slice.RequestBody);
        var body = Json(slice.RequestBody!);
        Assert.True(body.GetProperty("required").GetBoolean());
        var props = body.GetProperty("schema").GetProperty("properties").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "name", "comment" }, props);
        Assert.Equal("object", body.GetProperty("schema").GetProperty("type").GetString());
    }

    // Inline specs (the shipped ones) keep working exactly as before.
    [Fact]
    public void Inline_parameters_still_work()
    {
        const string inline = """
            openapi: 3.0.3
            info: { title: t, version: '1' }
            paths:
              /devices:
                get:
                  operationId: devices_list
                  parameters:
                    - name: limit
                      in: query
                      schema: { type: integer }
                  responses:
                    "200":
                      description: ok
            """;

        var slice = OperationYamlSlicer.ExtractDetail(inline, "GET", "/devices");

        Assert.Single(slice.Parameters);
        Assert.Equal("ok", slice.ResponsePreview);
    }
}
