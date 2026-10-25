export type ChannelKind = 'discord' | 'pushover';

export const channelKindLabels: Record<ChannelKind, string> = { discord: 'Discord', pushover: 'Pushover' };

/** A Channel as the backend lists it, which never includes its secrets. */
export interface Channel {
  id: string;
  name: string;
  kind: ChannelKind;
}

/** A new Channel and the secrets it is sent with, of which only its kind's are used. */
export interface NewChannel {
  kind: ChannelKind;
  name: string;
  webhookUrl: string;
  userKey: string;
  appToken: string;
}
