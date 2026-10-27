import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";

// An update is only a hint; all visible data is reloaded through authorized REST reads.
export function connectDeploymentUpdates(token: string, invalidate: () => void) {
  const connection = new HubConnectionBuilder()
    .withUrl("/hubs/ops", { accessTokenFactory: () => token })
    .withAutomaticReconnect()
    .build();
  let stopped = false;
  let retry: ReturnType<typeof setTimeout> | undefined;
  connection.on("DeploymentChanged", invalidate);
  connection.onreconnected(invalidate);
  connection.onclose(() => {
    if (!stopped) retry = setTimeout(start, 5000);
  });
  function start() {
    if (stopped || connection.state !== HubConnectionState.Disconnected) return;
    void connection.start().then(() => {
      if (!stopped) invalidate();
    }).catch(() => {
      if (!stopped) retry = setTimeout(start, 5000);
    });
  }
  start();
  return () => {
    stopped = true;
    clearTimeout(retry);
    void connection.stop();
  };
}
