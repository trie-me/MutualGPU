import { randomUUID } from "node:crypto";
import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const apiBaseUrl = required("MUTUALGPU_API_URL");
const presharedKey = required("MUTUALGPU_PROVIDER_KEY");
const executionUnitId = required("MUTUALGPU_EXECUTION_UNIT_ID");
const capabilityName = process.env.MUTUALGPU_SMOKE_CAPABILITY ?? "mutualgpu-node-api-smoke";
const compute = process.env.MUTUALGPU_SMOKE_COMPUTE ?? "Large";
const memory = process.env.MUTUALGPU_SMOKE_MEMORY ?? "Large";

const apiUrl = new URL(apiBaseUrl);
if (apiUrl.protocol !== "https:") throw new TypeError("MUTUALGPU_API_URL must use https.");
if (!isGuid(executionUnitId)) throw new TypeError("MUTUALGPU_EXECUTION_UNIT_ID must be a UUID configured by the API host.");

// The server owns the capability ID and normalizes the contract hash. The ID here
// merely satisfies the enrollment JSON shape.
const definition = {
  machine: { compute, memory },
  capabilities: [{
    id: randomUUID(),
    name: capabilityName,
    inputs: [],
    output: { hasMetadata: true },
    contractHash: "server-computed",
    description: "Minimal Node SDK to MutualGPU API gRPC smoke capability."
  }]
};

const transport = new NodeGrpcTransport(apiUrl, presharedKey, apiUrl);
const provider = new ProviderClient(transport);

await provider.enroll(definition);
await provider.connect(async task => {
  // This test intentionally does not submit or run work. Should another local
  // client submit a task during the handshake, decline it cleanly.
  await task.reject("The Node API smoke test does not execute tasks.");
});
provider.close();
console.log("MutualGPU Node SDK passed real API gRPC Enroll and Connect.");

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

function isGuid(value) {
  // Match Guid.TryParse on the API rather than imposing an RFC UUID version.
  // The documented local provider identity is a valid all-zero-prefix .NET GUID.
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
