import { useCallback, useEffect, useState } from "react";
import Waveform from "../../components/waveform";
import { Col, Row } from "antd";
import {
  CreateFileDto,
  FileDto,
  Fragment,
  transformText,
} from "@repo/shared/index";
import BarComponent from "../../components/Bar";
import { useAlert } from "../alerts/AlertsContext";
import { alertMarkerPositions, selectionToFragment } from "../alerts/alertTimeline";
import { withAuthToken } from "../../services/authToken";

interface AudioEditProps {
  audioFile: string;
  segmentData: FileDto;
  onCreateFragmentDto: (
    createFragmentDto: CreateFileDto,
    fragment: Fragment
  ) => void;
}
const AudioEdit: React.FC<AudioEditProps> = ({
  audioFile,
  segmentData,
  onCreateFragmentDto: onCreateFileDto,
}) => {
  const { selectedAlert } = useAlert();

  const url = withAuthToken(
    `${import.meta.env.VITE_API_LOCAL}/audio/fetchByName/${audioFile}`,
  );

  const [createFileDto, setCreateFileDto] = useState<CreateFileDto>(
    new CreateFileDto()
  );
  const [fragment, setFragment] = useState<Fragment>(new Fragment());

  const onSelection = useCallback(
    (start: number, end: number) => {
      const selection = selectionToFragment(selectedAlert, segmentData, start, end);
      setFragment((prevFragment) => ({ ...prevFragment, ...selection.fragment }));
      setCreateFileDto((prevCreateFileDto) => ({
        ...prevCreateFileDto,
        ...selection.createFileDto,
      }));
    },
    [selectedAlert, segmentData]
  );

  useEffect(() => {
    if (createFileDto.alert) {
      onCreateFileDto(createFileDto, fragment);
    }
  }, [createFileDto, onCreateFileDto, fragment]);

  if (!selectedAlert) return <div>No alert selected</div>;

  const { startPosition, endPosition } = alertMarkerPositions(selectedAlert, segmentData);

  return (
    <Row align="middle">
      <Col span={24} style={{ marginBottom: "12px" }}>
        <div
          style={{ whiteSpace: "normal" }}
          dangerouslySetInnerHTML={{
            __html: transformText(
              selectedAlert.text ?? "",
              selectedAlert.words ?? []
            ),
          }}
        />
      </Col>
      <Col span={24}>
        <Waveform url={url} onSelection={onSelection} />
        <BarComponent startPosition={startPosition} endPosition={endPosition} />
      </Col>
    </Row>
  );
};

export default AudioEdit;
