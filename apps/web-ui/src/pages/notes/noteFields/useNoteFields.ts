import React from "react";
import { Form } from "antd";
import type { FormInstance } from "antd";
import dayjs from "dayjs";
import { useQuery } from "react-query";
import api from "../../../services/Agent";
import type { MediaTypeDto, NoteDto, PlatformDto } from "@repo/shared/index";
import {
  acceptByMedia,
  expectedExtension,
  isAttachmentAllowed,
  isRadioOrTelevisionMedia,
  normalizeMedia,
} from "./noteMedia";

interface ClientTopicNode {
  name?: string;
  subtopics?: Array<{
    name?: string;
    subsubtopics?: Array<{ name?: string }>;
  }>;
}

interface ClientWithTopics {
  id?: string;
  name?: string;
  topics?: ClientTopicNode[];
}

export interface NoteFieldsOptions {
  // The id of the note being edited (its own consecutive is not counted as taken).
  noteId?: string;
  // The medium, outlet and client come from an alert and must not be reset to catalogue values.
  lockContext?: boolean;
  // Compute the NOTA consecutive for every medium, not only radio and television.
  indexForAllMedia?: boolean;
}

/**
 * The logic behind a note's fields, shared by the manual note form and the editors opened from an
 * alert: the catalogues (media, platforms, clients), the topic cascade, the program slot nearest
 * to the time (with its audio label, rate and audience), the consecutive NOTA number and the
 * value. It reads and writes the given antd form, so the fields it fills (index, program,
 * audioLabel, rate, audience, value, zone, city, topic...) are ordinary form fields.
 */
export function useNoteFields(form: FormInstance, options: NoteFieldsOptions = {}) {
  const { noteId, lockContext = false, indexForAllMedia = false } = options;
  const selectedMedia = Form.useWatch("media", form) as string | undefined;
  const selectedDate = Form.useWatch("date", form) as dayjs.Dayjs | undefined;
  const selectedStartTime = Form.useWatch("startTime", form) as
    | dayjs.Dayjs
    | undefined;
  const selectedProgram = Form.useWatch("program", form) as string | undefined;
  const selectedIndex = Form.useWatch("index", form) as string | undefined;
  const selectedRate = Form.useWatch("rate", form) as
    | string
    | number
    | undefined;
  const selectedDuration = Form.useWatch("duration", form) as
    | string
    | number
    | undefined;
  const selectedAttachment = Form.useWatch("attachment", form) as
    | string
    | undefined;
  // A link here is the PDF the system generated and archived for a web note: shown, never replaced.
  const hasGeneratedPdfLink = /^https?:\/\//i.test(selectedAttachment ?? "");
  const selectedMediaName = Form.useWatch("mediaName", form) as
    | string
    | undefined;
  const selectedClientName = Form.useWatch("clientName", form) as
    | string
    | undefined;
  const selectedTopicName = Form.useWatch("topic", form) as string | undefined;
  const selectedSubtopicName = Form.useWatch("subtopic", form) as
    | string
    | undefined;

  const { data: mediaTypes = [] } = useQuery<MediaTypeDto[]>(
    ["media-types"],
    async () => {
      const res = await api.get("/clients/media/all");
      return res.data as MediaTypeDto[];
    },
    { staleTime: Infinity },
  );

  const { data: platforms = [] } = useQuery<PlatformDto[]>(
    ["platforms-by-media", selectedMedia],
    async () => {
      const res = await api.get(`/settings/get-platforms/${selectedMedia}`);
      return res.data as PlatformDto[];
    },
    { enabled: !!selectedMedia, staleTime: 5 * 60 * 1000 },
  );

  const { data: clients = [] } = useQuery<ClientWithTopics[]>(
    ["clients"],
    async () => {
      const res = await api.get("/clients");
      return res.data as ClientWithTopics[];
    },
    { staleTime: 5 * 60 * 1000 },
  );

  const selectedDateText = React.useMemo(() => {
    if (!selectedDate?.isValid()) return undefined;
    return selectedDate.format("YYYY-MM-DD");
  }, [selectedDate]);

  const { data: notesByDate = [] } = useQuery<NoteDto[]>(
    ["notes-by-date-media", selectedDateText, selectedMedia],
    async () => {
      if (!selectedDateText) return [];
      const res = await api.post("/notes/list", {
        startDate: selectedDateText,
        endDate: selectedDateText,
      });
      return (res.data ?? []) as NoteDto[];
    },
    {
      enabled: !!selectedDateText && !!selectedMedia,
      staleTime: 30 * 1000,
    },
  );

  const selectedClient = React.useMemo(
    () => clients.find((client) => client.name === selectedClientName),
    [clients, selectedClientName],
  );

  const topicOptions = React.useMemo(
    () =>
      (selectedClient?.topics ?? [])
        .filter((topic) => topic?.name)
        .map((topic) => ({ value: topic.name, label: topic.name })),
    [selectedClient],
  );

  const selectedTopic = React.useMemo(
    () =>
      (selectedClient?.topics ?? []).find(
        (topic) => topic.name === selectedTopicName,
      ),
    [selectedClient, selectedTopicName],
  );

  const subtopicOptions = React.useMemo(
    () =>
      (selectedTopic?.subtopics ?? [])
        .filter((subtopic) => subtopic?.name)
        .map((subtopic) => ({ value: subtopic.name, label: subtopic.name })),
    [selectedTopic],
  );

  const selectedSubtopic = React.useMemo(
    () =>
      (selectedTopic?.subtopics ?? []).find(
        (subtopic) => subtopic.name === selectedSubtopicName,
      ),
    [selectedTopic, selectedSubtopicName],
  );

  const subsubtopicOptions = React.useMemo(
    () =>
      (selectedSubtopic?.subsubtopics ?? [])
        .filter((subsubtopic) => subsubtopic?.name)
        .map((subsubtopic) => ({
          value: subsubtopic.name,
          label: subsubtopic.name,
        })),
    [selectedSubtopic],
  );

  const disableSubtopic = !selectedTopicName || subtopicOptions.length === 0;
  const disableSubsubtopic =
    !selectedSubtopicName || subsubtopicOptions.length === 0;

  const toMinutes = React.useCallback((value?: string): number => {
    if (!value || !/^\d{2}:\d{2}$/.test(value)) return -1;
    const [hoursText, minutesText] = value.split(":");
    const hours = Number(hoursText);
    const minutes = Number(minutesText);
    if (
      Number.isNaN(hours) ||
      Number.isNaN(minutes) ||
      hours < 0 ||
      hours > 23 ||
      minutes < 0 ||
      minutes > 59
    ) {
      return -1;
    }
    return hours * 60 + minutes;
  }, []);

  const toPositiveNumber = React.useCallback(
    (value: unknown): number | null => {
      const numeric = Number(value);
      if (!Number.isFinite(numeric) || numeric <= 0) return null;
      return numeric;
    },
    [],
  );

  const isRadioOrTelevision = React.useMemo(
    () => isRadioOrTelevisionMedia(selectedMedia),
    [selectedMedia],
  );

  const selectedPlatform = React.useMemo(
    () => platforms.find((platform) => platform.name === selectedMediaName),
    [platforms, selectedMediaName],
  );

  const selectedDayType = React.useMemo(() => {
    if (!selectedDate?.isValid()) return undefined;
    const dayNumber = selectedDate.day();
    if (dayNumber === 0) return "sunday";
    if (dayNumber === 6) return "saturday";
    return "weekday";
  }, [selectedDate]);

  const selectedStartTimeText = React.useMemo(() => {
    if (!selectedStartTime) return undefined;
    return selectedStartTime.format("HH:mm");
  }, [selectedStartTime]);

  const attachmentExtension = React.useMemo(() => {
    const fallback = expectedExtension(selectedMedia);
    if (!selectedAttachment) return fallback;
    const extensionMatch = /\.([a-zA-Z0-9]+)$/.exec(selectedAttachment);
    return extensionMatch?.[1]?.toLowerCase() ?? fallback;
  }, [selectedAttachment, selectedMedia]);

  const daySlots = React.useMemo(() => {
    if (!selectedPlatform || !selectedDayType) return [];
    return [...(selectedPlatform.slots ?? [])]
      .filter((slot) => slot.day === selectedDayType)
      .sort((a, b) => a.start.localeCompare(b.start));
  }, [selectedPlatform, selectedDayType]);

  const closestSlotIndex = React.useMemo(() => {
    if (!daySlots.length || !selectedStartTimeText) return -1;

    const currentMinutes = toMinutes(selectedStartTimeText);
    if (currentMinutes < 0) return -1;

    let bestIndex = -1;
    let bestDistance = Number.POSITIVE_INFINITY;

    daySlots.forEach((slot, index) => {
      const start = toMinutes(slot.start);
      const end = toMinutes(slot.end);
      if (start < 0 || end < 0) return;

      const isWithin =
        end === 1439
          ? currentMinutes >= start && currentMinutes <= end
          : currentMinutes >= start && currentMinutes < end;

      const distance = isWithin
        ? 0
        : Math.min(
            Math.abs(currentMinutes - start),
            Math.abs(currentMinutes - end),
          );

      if (distance < bestDistance) {
        bestDistance = distance;
        bestIndex = index;
      }
    });

    return bestIndex;
  }, [daySlots, selectedStartTimeText, toMinutes]);

  const programOptions = React.useMemo(() => {
    if (closestSlotIndex < 0 || !daySlots.length) return [];

    const candidateIndexes = [
      closestSlotIndex - 1,
      closestSlotIndex,
      closestSlotIndex + 1,
    ].filter((index) => index >= 0 && index < daySlots.length);

    return candidateIndexes.map((index) => {
      const slot = daySlots[index];
      const slotLabel = `${slot.start} - ${slot.end} - (${slot.label ?? ""})`;
      return {
        value: slotLabel,
        label: slotLabel,
        slot,
      };
    });
  }, [closestSlotIndex, daySlots]);

  const programSlotMap = React.useMemo(
    () => new Map(programOptions.map((item) => [item.value, item.slot])),
    [programOptions],
  );

  const closestProgramValue = React.useMemo(() => {
    if (!programOptions.length) return undefined;
    const middleIndex = Math.min(1, programOptions.length - 1);
    return programOptions[middleIndex]?.value;
  }, [programOptions]);

  const hasProgramContext =
    !!selectedDayType && !!selectedStartTimeText && programOptions.length > 0;

  const effectiveProgramValue = React.useMemo(() => {
    if (!hasProgramContext) return undefined;
    const optionExists = programOptions.some(
      (option) => option.value === selectedProgram,
    );
    if (optionExists) return selectedProgram;
    return closestProgramValue ?? programOptions[0]?.value;
  }, [hasProgramContext, programOptions, selectedProgram, closestProgramValue]);

  const selectedProgramSlot = React.useMemo(() => {
    if (!effectiveProgramValue) return undefined;
    return programSlotMap.get(effectiveProgramValue);
  }, [programSlotMap, effectiveProgramValue]);

  const composedAudioLabel = React.useMemo(() => {
    const resolvedIndex =
      selectedIndex && selectedIndex.trim().length > 0 ? selectedIndex : "##";
    if (!selectedProgramSlot?.audioLabel) return undefined;
    return `NOTA_${resolvedIndex}${selectedProgramSlot.audioLabel}.${attachmentExtension}`;
  }, [selectedIndex, selectedProgramSlot, attachmentExtension]);

  const canSelectTime =
    !!selectedMedia && !!selectedDate && selectedDate.isValid();

  const getFileDurationInSeconds = React.useCallback(
    (file: File, media?: string): Promise<number | undefined> => {
      const extension = expectedExtension(media);
      if (extension !== "mp3" && extension !== "mp4") {
        return Promise.resolve(undefined);
      }

      return new Promise((resolve) => {
        const objectUrl = URL.createObjectURL(file);
        const element = document.createElement(
          extension === "mp4" ? "video" : "audio",
        );

        element.preload = "metadata";
        element.onloadedmetadata = () => {
          const duration = Number(element.duration);
          URL.revokeObjectURL(objectUrl);
          if (!Number.isFinite(duration) || duration <= 0) {
            resolve(undefined);
            return;
          }
          resolve(Math.max(1, Math.round(duration)));
        };

        element.onerror = () => {
          URL.revokeObjectURL(objectUrl);
          resolve(undefined);
        };

        element.src = objectUrl;
      });
    },
    [],
  );

  const nextIndex = React.useMemo(() => {
    if (!selectedDateText || !selectedMedia) return undefined;

    const normalizedSelectedMedia = normalizeMedia(selectedMedia);
    const maxIndex = notesByDate
      .filter(
        (current) =>
          current.date === selectedDateText &&
          normalizeMedia(current.media) === normalizedSelectedMedia &&
          current.id !== noteId,
      )
      .reduce((acc, current) => {
        const match = /\d+/.exec(current.index ?? "");
        const parsed = Number(match?.[0] ?? 0);
        return Number.isFinite(parsed) && parsed > acc ? parsed : acc;
      }, 0);

    return String(maxIndex + 1).padStart(2, "0");
  }, [selectedDateText, selectedMedia, notesByDate, noteId]);

  React.useEffect(() => {
    // An alert's medium and outlet are fixed: the outlet may not be in the platform catalogue.
    if (lockContext) return;
    if (!selectedMedia) {
      form.setFieldValue("mediaName", undefined);
      form.setFieldsValue({
        startTime: undefined,
        audioLabel: undefined,
        rate: undefined,
        audience: undefined,
        value: undefined,
        duration: undefined,
        index: undefined,
      });
      return;
    }

    const currentMediaName = form.getFieldValue("mediaName");
    if (!currentMediaName) {
      return;
    }

    const existsInCurrentMedia = platforms.some(
      (platform) => platform.name === currentMediaName,
    );

    if (!existsInCurrentMedia) {
      form.setFieldValue("mediaName", undefined);
    }
  }, [selectedMedia, platforms, form]);

  React.useEffect(() => {
    if (!selectedAttachment) {
      return;
    }

    if (!isAttachmentAllowed(selectedAttachment, selectedMedia)) {
      form.setFieldValue("attachment", undefined);
    }
  }, [selectedAttachment, selectedMedia, form]);

  React.useEffect(() => {
    if (!selectedMediaName) {
      form.setFieldsValue({ zone: undefined, city: undefined });
      return;
    }

    form.setFieldsValue({
      zone: selectedPlatform?.zone ?? undefined,
      city: selectedPlatform?.city ?? undefined,
    });
  }, [selectedMediaName, selectedPlatform, form]);

  React.useEffect(() => {
    if (!selectedPlatform) {
      // A web outlet from an alert may not be in the platform catalogue: what the alert and the
      // saved note already say about it must survive that.
      if (lockContext && !isRadioOrTelevision) return;
      form.setFieldsValue({
        program: undefined,
        audioLabel: undefined,
        rate: undefined,
        audience: undefined,
        value: undefined,
        duration: undefined,
      });
      return;
    }

    if (!isRadioOrTelevision) {
      form.setFieldsValue({
        audioLabel: undefined,
        rate:
          selectedPlatform.rate == null
            ? undefined
            : String(selectedPlatform.rate),
        audience:
          selectedPlatform.audience == null
            ? undefined
            : String(selectedPlatform.audience),
        value: undefined,
        duration: undefined,
      });
    }
  }, [selectedPlatform, isRadioOrTelevision, lockContext, form]);

  React.useEffect(() => {
    if (!isRadioOrTelevision || !selectedPlatform) {
      return;
    }

    if (!hasProgramContext) {
      form.setFieldsValue({
        program: undefined,
        audioLabel: undefined,
        rate: undefined,
        audience: undefined,
        value: undefined,
      });
      return;
    }

    if (effectiveProgramValue && effectiveProgramValue !== selectedProgram) {
      form.setFieldValue("program", effectiveProgramValue);
    }

    form.setFieldsValue({
      audioLabel: composedAudioLabel,
      rate:
        selectedProgramSlot?.rate == null
          ? undefined
          : String(selectedProgramSlot.rate),
      audience:
        selectedProgramSlot?.audience == null
          ? undefined
          : String(selectedProgramSlot.audience),
    });
  }, [
    isRadioOrTelevision,
    selectedPlatform,
    hasProgramContext,
    selectedProgram,
    effectiveProgramValue,
    composedAudioLabel,
    selectedProgramSlot,
    form,
  ]);

  React.useEffect(() => {
    if (!isRadioOrTelevision) {
      return;
    }

    const rateNumber = toPositiveNumber(selectedRate);
    const durationNumber = toPositiveNumber(selectedDuration);
    if (rateNumber == null || durationNumber == null) {
      form.setFieldValue("value", undefined);
      return;
    }

    const computedValue = (rateNumber * durationNumber) / 30;
    form.setFieldValue("value", String(computedValue));
  }, [
    isRadioOrTelevision,
    selectedRate,
    selectedDuration,
    form,
  ]);

  React.useEffect(() => {
    if (!isRadioOrTelevision && !indexForAllMedia) {
      form.setFieldValue("index", undefined);
      return;
    }

    if (!nextIndex) return;

    const isEditing = !!noteId;
    if (isEditing && selectedIndex) {
      return;
    }

    form.setFieldValue("index", nextIndex);
  }, [isRadioOrTelevision, indexForAllMedia, nextIndex, noteId, selectedIndex, form]);

  React.useEffect(() => {
    if (lockContext || !clients.length) {
      return;
    }

    const currentClientName = form.getFieldValue("clientName") as
      | string
      | undefined;
    if (!currentClientName) {
      form.setFieldsValue({ clientName: clients[0]?.name });
      return;
    }

    const exists = clients.some((client) => client.name === currentClientName);
    if (!exists) {
      form.setFieldsValue({
        clientName: clients[0]?.name,
        topic: undefined,
        subtopic: undefined,
        subsubtopic: undefined,
      });
    }
  }, [clients, form]);

  React.useEffect(() => {
    if (!selectedClientName) {
      form.setFieldsValue({
        topic: undefined,
        subtopic: undefined,
        subsubtopic: undefined,
      });
      return;
    }

    const currentTopic = form.getFieldValue("topic") as string | undefined;
    const validTopic = (selectedClient?.topics ?? []).some(
      (topic) => topic.name === currentTopic,
    );
    if (!validTopic) {
      form.setFieldsValue({
        topic: undefined,
        subtopic: undefined,
        subsubtopic: undefined,
      });
    }
  }, [selectedClientName, selectedClient, form]);

  React.useEffect(() => {
    const currentSubtopic = form.getFieldValue("subtopic") as
      | string
      | undefined;
    const validSubtopic = (selectedTopic?.subtopics ?? []).some(
      (subtopic) => subtopic.name === currentSubtopic,
    );
    if (!validSubtopic) {
      form.setFieldsValue({ subtopic: undefined, subsubtopic: undefined });
    }
  }, [selectedTopic, form]);

  React.useEffect(() => {
    const currentSubsubtopic = form.getFieldValue("subsubtopic") as
      | string
      | undefined;
    const validSubsubtopic = (selectedSubtopic?.subsubtopics ?? []).some(
      (subsubtopic) => subsubtopic.name === currentSubsubtopic,
    );
    if (!validSubsubtopic) {
      form.setFieldValue("subsubtopic", undefined);
    }
  }, [selectedSubtopic, form]);

  return {
    mediaTypes,
    platforms,
    clients,
    selectedMedia,
    selectedAttachment,
    selectedClientName,
    hasGeneratedPdfLink,
    isRadioOrTelevision,
    topicOptions,
    subtopicOptions,
    subsubtopicOptions,
    disableSubtopic,
    disableSubsubtopic,
    programOptions,
    canSelectTime,
    getFileDurationInSeconds,
    getExpectedExtension: expectedExtension,
    getAcceptByMedia: acceptByMedia,
    isAttachmentAllowed,
  };
}

export type { NoteDto };
