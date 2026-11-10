import { HubConnectionBuilder, HubConnectionState } from "@microsoft/signalr";

// An update is only a hint; all visible data is reloaded through authorized REST reads.
//
// State machine:
//   connecting --start ok--> live            (one catch-up `invalidate`)
//   connecting --start failed--> polling     (retry start every 5 s)
//   live --transport lost--> polling         (SignalR automatic reconnect running)
//   polling --reconnected / restarted--> live (one catch-up `invalidate`)
//   live/polling --closed--> polling         (retry start every 5 s)
// Views poll REST every `pollingFallbackMs` only while `shouldPoll(status)`; while live they rely on hub messages.
export type UpdateConnectionStatus = "connecting" | "live" | "polling";

export const pollingFallbackMs = 10_000;

/** Fallback polling runs only while the hub is disconnected or reconnecting, never while it is live. */
export function shouldPoll(status: UpdateConnectionStatus) {
  return status === "polling";
}

export function connectDeploymentUpdates(
  token: string,
  invalidate: () => void,
  onStatus: (status: UpdateConnectionStatus) => void,
  approvalHint: (id: string) => void,
) {
  const connection = new HubConnectionBuilder()
    .withUrl("/hubs/ops", { accessTokenFactory: () => token })
    .withAutomaticReconnect()
    .build();
  let stopped = false;
  let retry: ReturnType<typeof setTimeout> | undefined;
  connection.on("DeploymentChanged", invalidate);
  connection.on("ApprovalQueueChanged", approvalHint);
  connection.onreconnecting(() => onStatus("polling"));
  connection.onreconnected(() => {
    onStatus("live");
    invalidate();
  });
  connection.onclose(() => {
    if (!stopped) {
      onStatus("polling");
      retry = setTimeout(start, 5000);
    }
  });
  function start() {
    if (stopped || connection.state !== HubConnectionState.Disconnected) return;
    void connection
      .start()
      .then(() => {
        if (!stopped) {
          onStatus("live");
          invalidate();
        }
      })
      .catch(() => {
        if (!stopped) {
          onStatus("polling");
          retry = setTimeout(start, 5000);
        }
      });
  }
  start();
  return () => {
    stopped = true;
    clearTimeout(retry);
    void connection.stop();
  };
}
