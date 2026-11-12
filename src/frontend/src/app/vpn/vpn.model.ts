/** Mirrors `VpnSettingsDto` in src/Core/VpnSettings.cs. */
export interface VpnSettingsDto {
  /** Whether the VPN switches region every interval. */
  rotationEnabled: boolean;
  rotationIntervalMinutes: number;
  /** How far each rotation may land before or after the interval, picked at random each time. */
  rotationJitterMinutes: number;
  /** Regions rotation never picks. A manual switch can still go to them. */
  excludedRegions: string[];
}

/** Mirrors `VpnSettings` in src/Core/VpnSettings.cs. Times are UTC ISO strings. */
export interface VpnSettings extends VpnSettingsDto {
  id: number;
  /** The region the VPN was last switched to; `null` until the first switch, when gluetun picks any. */
  currentRegion: string | null;
  lastSwitchTime: string | null;
  /** `null` while rotation is off. */
  nextRotationTime: string | null;
  /** Why the last switch failed, cleared by the next one that succeeds. */
  lastError: string | null;
}

/** Mirrors `VpnSwitchProgress` in src/Backend/VpnServices/VpnRotationService.cs. */
export interface VpnSwitchProgress {
  region: string;
  /** Whether the switch is waiting for the VPN watch runs in progress to finish first. */
  waitingForRun: boolean;
  startedTime: string;
}

/** Mirrors `GluetunPublicIp` in src/Backend/VpnServices/GluetunHttpClient.cs. The location is from IP geolocation. */
export interface VpnPublicIp {
  publicIp: string;
  country: string | null;
  region: string | null;
  city: string | null;
  organization: string | null;
}

/** Mirrors `VpnStatus` in src/Backend/Controllers/VpnController.cs. */
export interface VpnStatus {
  /** Whether the Backend knows where gluetun is. If not, every other field is `null`. */
  configured: boolean;
  settings: VpnSettings | null;
  /** The region switch in progress, if any. */
  switch: VpnSwitchProgress | null;
  /** `null` while the tunnel is down, if gluetun couldn't look the IP up, or if gluetun is unreachable. */
  publicIp: VpnPublicIp | null;
  gluetunError: string | null;
}

/** Mirrors `VpnRegions` in src/Backend/VpnServices/VpnRegionCatalog.cs. */
export interface VpnRegions {
  provider: string;
  regions: string[];
  /** Whether `GLUETUN_SERVER_REGIONS` limits the regions, so the provider may have more. */
  limited: boolean;
}
