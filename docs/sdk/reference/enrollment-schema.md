# Provider enrollment schema

`ProviderClient.enroll(definition)` submits the complete current definition for one execution unit. A later call replaces that definition; capabilities omitted from the new definition are removed from future scheduling. Existing task attempts remain attached to the capability snapshot selected when they were submitted.

The SDK supplies server-owned capability IDs and contract hashes. Consumers must not add or persist those fields.

## Top-level definition

```js
{
  machine: {
    tier: "Large",
    specifications: { computeTier: "Large", memoryGiB: 32 }
  },
  capabilities: [/* capability definitions */]
}
```

| Property | Type | Rules |
| --- | --- | --- |
| `machine` | object | Required |
| `capabilities` | array | Required; capability names must be unique using ordinal, case-sensitive comparison |

## Machine

`machine.tier` is provider classification metadata. It must be one of `Small`, `Medium`, `Large`, or `ExtraLarge`. `Automatic` is a requestor choice and cannot be enrolled by a provider.

`machine.specifications` is capacity visible to requestors and used by scheduling. A provider can satisfy a request for an equal or smaller `computeTier` and `memoryGiB`.

| `computeTier` | Valid `memoryGiB` |
| --- | --- |
| `Small` | 8–16 |
| `Medium` | 8–24 |
| `Large` | 8–48 |
| `ExtraLarge` | 8–128 |

Memory must be a whole number of GiB.

## Capability

```js
{
  name: "example-renderer",
  description: "Produces a rendered asset bundle.",
  inputs: [],
  output: {}
}
```

| Property | Type | Rules |
| --- | --- | --- |
| `name` | string | Required, non-blank, unique within the enrollment |
| `description` | string | Optional presentation text |
| `inputs` | array | Required; keys must be unique and at most one input may have type `Image` |
| `output` | object | Required; use `{}` when the ZIP is the only output |

Changing presentation text or display order does not change capability compatibility. Changing input types, required/default/range values, allowed values, content types, or output declarations changes the execution contract and may produce an enrollment conflict with an existing capability of the same name.

## Input

```js
{
  key: "quality",
  type: "Integer",
  required: false,
  label: "Quality",
  description: "Render quality percentage.",
  default: "80",
  minimum: 1,
  maximum: 100,
  displayOrder: 0
}
```

| Property | Type | Rules |
| --- | --- | --- |
| `key` | string | Required, non-blank, stable, unique within the capability; used in `task.scalars` |
| `type` | string | `String`, `Integer`, `Number`, `Boolean`, `Date`, `DateTime`, `DateTimeOffset`, or `Image` |
| `required` | boolean | Required |
| `label` | string | Required consumer-facing label |
| `description` | string | Optional help text |
| `default` | string | Optional scalar default represented in wire format |
| `minimum` | number | Optional inclusive numeric minimum; cannot exceed `maximum` |
| `maximum` | number | Optional inclusive numeric maximum |
| `allowedValues` | string array | Optional and valid only for `String` inputs |
| `contentTypes` | string array | Optional and valid only for `Image` inputs |
| `displayOrder` | integer | Optional presentation order; defaults to `0` |

All assigned scalar values arrive as strings. Consumer handlers remain responsible for parsing them and validating them before execution.

## Output

Every successful task requires a valid ZIP result. Optional parts are declared with:

```js
{
  hasThumbnail: true,
  hasPreview: true,
  hasMetadata: true,
  hasLogs: true,
  previewContentTypes: ["image/webp", "image/png"],
  metadataSchema: "{\"type\":\"object\"}"
}
```

| Property | Type | Meaning |
| --- | --- | --- |
| `hasThumbnail` | boolean | Allows a PNG, JPEG, or WebP thumbnail |
| `hasPreview` | boolean | Allows a preview image |
| `hasMetadata` | boolean | Allows a JSON object metadata part |
| `hasLogs` | boolean | Allows UTF-8 plain-text logs |
| `previewContentTypes` | string array | MIME types accepted for the preview; the uploaded preview must match one |
| `metadataSchema` | string | Optional schema contract metadata; the demo does not perform complete JSON Schema evaluation |

Uploading any optional part that was not declared ends the current attempt as a result-validation failure.
