import React, { useState } from "react";
import { Button, Divider, Select, message } from "antd";
import { PlusOutlined } from "@ant-design/icons";

interface Option {
  value?: string;
  label?: string;
}

// Case and accents do not matter when searching ("inversion" finds "Inversión").
export const fold = (value?: string): string =>
  (value ?? "").normalize("NFD").replace(/\p{M}/gu, "").trim().toLowerCase();

// The text can be anywhere in the option, not only at its start.
export const matchesAnywhere = (input: string, option?: Option): boolean =>
  fold(option?.label).includes(fold(input));

interface TopicSelectProps {
  value?: string;
  onChange?: (value?: string) => void;
  placeholder: string;
  options: Option[];
  disabled?: boolean;
  // When given, a name that is not in the list can be added from the search box.
  onAdd?: (name: string) => Promise<void>;
}

// A select for the note's Tema / Subtema / Subsubtema: searchable by any part of the word and, for the subtopics, with
// the option of adding a new one that did not exist yet.
const TopicSelect: React.FC<TopicSelectProps> = ({ value, onChange, placeholder, options, disabled, onAdd }) => {
  const [search, setSearch] = useState("");
  const [adding, setAdding] = useState(false);

  const typed = search.trim();
  const canAdd = !!onAdd && typed.length > 0 && !options.some((option) => fold(option.label) === fold(typed));

  const add = async () => {
    if (!onAdd) return;
    setAdding(true);
    try {
      await onAdd(typed);
      setSearch("");
    } catch (error) {
      message.error(
        (error as { response?: { data?: { message?: string; error?: string } } })?.response?.data?.message ??
          "No se pudo agregar",
      );
    } finally {
      setAdding(false);
    }
  };

  return (
    <Select
      showSearch
      allowClear
      value={value}
      onChange={(next) => onChange?.(next)}
      placeholder={placeholder}
      options={options}
      disabled={disabled}
      filterOption={matchesAnywhere}
      searchValue={search}
      onSearch={setSearch}
      onBlur={() => setSearch("")}
      notFoundContent={onAdd ? "Escribe el nombre y agrégalo" : "Sin resultados"}
      popupRender={(menu) => (
        <>
          {menu}
          {canAdd && (
            // Pressing the button must not take the focus from the search box first: that blur cleared the search,
            // the button disappeared with it and the click never arrived (nothing was added).
            <div onMouseDown={(event) => event.preventDefault()}>
              <Divider style={{ margin: "4px 0" }} />
              <Button type="link" icon={<PlusOutlined />} loading={adding} onClick={add} style={{ width: "100%", textAlign: "left" }}>
                Agregar «{typed}»
              </Button>
            </div>
          )}
        </>
      )}
    />
  );
};

export default TopicSelect;
