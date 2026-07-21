import { randomUUID } from "node:crypto";
import { ProviderClient } from "@mutualgpu/provider-core";
import { NodeGrpcTransport } from "@mutualgpu/provider-node";

const apiBaseUrl = required("MUTUALGPU_API_URL");
const presharedKey = required("MUTUALGPU_PROVIDER_KEY");
const executionUnitId = required("MUTUALGPU_EXECUTION_UNIT_ID");
const apiUrl = new URL(apiBaseUrl);
if (apiUrl.protocol !== "https:") throw new TypeError("MUTUALGPU_API_URL must use https.");
if (!isGuid(executionUnitId)) throw new TypeError("MUTUALGPU_EXECUTION_UNIT_ID must be a GUID configured by the API host.");

const definition = {
  machine: { compute: "Large", memory: "Large" },
  capabilities: [{
    id: randomUUID(),
    name: "mutualgpu-local-demo",
    inputs: [],
    output: { hasMetadata: true },
    contractHash: "server-computed",
    description: "Synthetic local demo capability. It proves the exchange, not GPU computation."
  }]
};

const provider = new ProviderClient(new NodeGrpcTransport(apiUrl, presharedKey, apiUrl));
await provider.enroll(definition);
console.log("MutualGPU local demo provider is connected. Open the requestor UI and submit a task.");

await provider.connect(async task => {
  console.log(`Running synthetic demo task ${task.taskId}.`);
  await task.accept();
  await task.reportProgress({ phase: "demo", percent: 50, message: "Generating a synthetic result." });

  // Valid empty ZIP (EOCD only). The API validates this exactly as it validates a
  // real handler result, while making no claim that local GPU work took place.
  const resultZip = Uint8Array.from(Buffer.from("UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==", "base64"));
  const { receipt } = await task.uploadResult({
    resultZip,
    metadata: {
      provider: "node-local-demo",
      taskId: task.taskId,
      message: "Synthetic local-demo result; no GPU workload was executed."
    }
  });
  await task.complete(receipt);
  console.log(`Completed synthetic demo task ${task.taskId}.`);
});

function required(name) {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

function isGuid(value) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}
