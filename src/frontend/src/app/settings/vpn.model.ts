/** The VPN that VPN Runs go through, as the backend last read it from gluetun */
export interface Vpn {
  /** Whether Lookout is configured with a VPN at all */
  enabled: boolean;
  /** Whether VPN Runs can go through it now; they're refused while it isn't */
  connected: boolean;
  region: string | null;
  exitIp: string | null;
  /** Why the VPN's status couldn't be read, if it couldn't */
  error: string | null;
}
