export interface ParamInfo {
  /** Recipe key, for example "exposure". */
  key: string;
  min: number;
  max: number;
}

/** Applies edit parameters (one per adjustment, in paramInfo order), redraws
 * the preview and returns the GPU time in ms. */
export const setParams: (values: number[]) => number;
/** Sets the filter id ('' for none), its strength (0-1) and the geometry,
 * redraws the preview and returns the GPU time in ms. */
export const setLook: (filter: string, strength: number, quarterTurns: number,
  flip: boolean, straighten: number) => number;
/** The built-in filters' ids. */
export const filterIds: () => string[];
/** The adjustments the core offers. */
export const paramInfo: () => ParamInfo[];
export const version: () => string;
