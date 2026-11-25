/* eslint-disable @typescript-eslint/no-explicit-any */
import React, { useEffect, useRef } from "react";
import WaveSurfer from "wavesurfer.js";
import RegionsPlugin from "wavesurfer.js/dist/plugins/regions.js";

import TimelinePlugin from "wavesurfer.js/dist/plugins/timeline.js";
import Minimap from "wavesurfer.js/dist/plugins/minimap.js";
import ZoomPlugin from "wavesurfer.js/dist/plugins/zoom.js";

interface WaveformProps {
  url: string;
  onSelection: (start: number, end: number) => void;
}

const Waveform: React.FC<WaveformProps> = ({ url, onSelection }) => {
  const waveformRef = useRef<HTMLDivElement | null>(null);
  const wsRef = useRef<WaveSurfer | null>(null);
  const regionRefs = useRef<any[]>([]);
  const regionSetRef = useRef<[boolean, boolean]>([false, false]);

  const createWaveSurfer = (url: string) => {
    const wavesurfer = WaveSurfer.create({
      container: waveformRef.current,
      url: url,
      waveColor: "#0080ff",
      progressColor: "#004080",
      cursorColor: "#ffffff",
      cursorWidth: 2,
      barWidth: 1,
      barGap: 1,
      barRadius: 1,
      barHeight: 0.7,
      minPxPerSec: 4, //El ancho del audio
      autoCenter: true,
      autoScroll: true,
      mediaControls: true,
    } as any);
    wavesurfer.registerPlugin(
      TimelinePlugin.create({
        height: 24,
        insertPosition: "beforebegin",
        timeInterval: 5,
        primaryLabelInterval: 60,
        secondaryLabelInterval: 60,
        style: {
          fontSize: "16px",
          color: "#2D5B88",
        },
      })
    );

    wavesurfer.registerPlugin(
      ZoomPlugin.create({
        scale: 0.5,
      })
    );

    wavesurfer.registerPlugin(
      Minimap.create({
        container: waveformRef.current,
        height: 20,
        barHeight: 0.5,
        waveColor: "#ddd",
        progressColor: "#999",
        // the Minimap takes all the same options as the wavesurferRef.current itself
        plugins: [
          TimelinePlugin.create({
            height: 10,
            timeInterval: 5,
            primaryLabelInterval: 60,
            secondaryLabelInterval: 30,
            style: {
              fontSize: "8px",
              color: "#2D5B88",
            },
            formatTimeCallback: function (secs) {
              const minutes = Math.floor(secs / 60) || 0;
              const seconds = secs - minutes * 60 || 0;
              return minutes + ":" + (seconds < 10 ? "0" : "") + seconds;
            },
          }),
        ],
      } as any)
    );

    return wavesurfer;
  };

  useEffect(() => {
    if (!waveformRef.current) return;

    regionRefs.current = [];
    regionSetRef.current = [false, false];
    wsRef.current = createWaveSurfer(url);

    const wsRegions = wsRef.current.registerPlugin(RegionsPlugin.create());
    const emitSelectionIfReady = () => {
      const [firstSet, secondSet] = regionSetRef.current;
      if (!firstSet || !secondSet) return;

      const [region1, region2] = regionRefs.current;
      if (!region1 || !region2) return;
      if (region1.start === region2.start) return;

      onSelection(
        Math.min(region1.start, region2.start),
        Math.max(region1.start, region2.start)
      );
    };

    const markRegionAsSet = (region: any) => {
      const index = regionRefs.current.findIndex((candidate) => candidate === region);
      if (index < 0) return;
      regionSetRef.current[index] = true;
    };

    wsRef.current.on("decode", () => {
      const region1 = wsRegions.addRegion({
        start: 0,
        color: "rgba(180, 180, 180, 0.5)",
      });
      const region2 = wsRegions.addRegion({
        start: 0,
        color: "rgba(180, 180, 180, 0.5)",
      });
      regionRefs.current = [region1, region2];
    });
    let touchtime = 0;
    let currentTime = 0;
    wsRef.current.on("click", () => {
      if (!wsRef.current) return;
      if (touchtime === 0) {
        // set first click
        wsRef.current.play();
        touchtime = new Date().getTime();
        currentTime = wsRef.current.getCurrentTime();
      } else if (
        new Date().getTime() - touchtime < 800 &&
        currentTime === wsRef.current.getCurrentTime()
      ) {
        // compare first click to this click and see if they occurred within double click threshold
        const pos = wsRef.current.getCurrentTime();
        // double click occurred

        const [region1, region2] = regionRefs.current;
        if (!region1 || !region2) return;
        const [firstSet, secondSet] = regionSetRef.current;
        const shouldUpdateRegion1 =
          !firstSet ||
          (secondSet && Math.abs(region1.start - pos) < Math.abs(region2.start - pos));

        if (shouldUpdateRegion1) {
          region1.setOptions({ start: pos });
          regionSetRef.current[0] = true;
        } else {
          region2.setOptions({ start: pos });
          regionSetRef.current[1] = true;
        }
        emitSelectionIfReady();

        touchtime = 0;
      } else {
        // not a double click so set as a new first click
        touchtime = new Date().getTime();
        currentTime = wsRef.current.getCurrentTime();
      }
    });

    wsRegions.on("region-updated", (region: any) => {
      markRegionAsSet(region);
      emitSelectionIfReady();
    });

    return () => {
      if (!wsRef.current) return;
      return wsRef.current.destroy();
    };
  }, [url, onSelection]);

  return <div ref={waveformRef} />;
};

export default Waveform;
